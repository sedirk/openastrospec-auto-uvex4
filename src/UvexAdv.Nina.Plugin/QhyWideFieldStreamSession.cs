using System.Threading.Channels;
using System.Net.Http;
using UvexAdv.Qhy.Core;
using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

/// <summary>One existing owner/job, bounded latest-wins analysis, joined retirement.
/// The analysis callback has no acquisition or shared-motion command capability.</summary>
internal sealed class QhyWideFieldStreamSession
{
    private readonly QhyServiceClient client;
    private readonly AcquisitionJobRequest request;
    private readonly Func<CancellationToken, Task> checkpoint;
    private readonly Func<QhyJobSnapshot, CancellationToken, Task> observe;
    private readonly Func<QhyJobSnapshot, QhyFrameRecord, CancellationToken, Task> analyze;
    private readonly CancellationTokenSource lifetime;
    private readonly Channel<(QhyJobSnapshot Job, QhyFrameRecord Frame)> queue =
        Channel.CreateBounded<(QhyJobSnapshot, QhyFrameRecord)>(new BoundedChannelOptions(1)
        { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest });
    private Task producer = Task.CompletedTask;
    private Task consumer = Task.CompletedTask;
    private Exception? terminalFailure;
    private Exception? retirementFailure;
    private Exception? analysisFailure;
    private QhyJobSnapshot snapshot;
    internal Guid JobId => snapshot.Id;
    internal QhyJobSnapshot Snapshot => Volatile.Read(ref snapshot);
    internal Exception? AnalysisFailure => Volatile.Read(ref analysisFailure);
    internal Exception? ProducerFailure => Volatile.Read(ref terminalFailure);
    internal long AnalyzedFrames => Interlocked.Read(ref analyzedFrames);
    private long analyzedFrames;
    internal bool IsJoined => producer.IsCompleted && consumer.IsCompleted && Snapshot.ExecutionJoined;

    private QhyWideFieldStreamSession(QhyServiceClient client, AcquisitionJobRequest request, QhyJobSnapshot job,
        Func<CancellationToken, Task> checkpoint, Func<QhyJobSnapshot, CancellationToken, Task> observe,
        Func<QhyJobSnapshot, QhyFrameRecord, CancellationToken, Task> analyze, CancellationToken token)
    {
        this.client = client; this.request = request; snapshot = job;
        this.checkpoint = checkpoint; this.observe = observe; this.analyze = analyze;
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var remaining = request.WideFieldStream!.DeadlineUtc - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero) lifetime.Cancel(); else lifetime.CancelAfter(remaining);
    }

    internal static async Task<QhyWideFieldStreamSession?> StartAsync(QhyServiceClient client,
        AcquisitionJobRequest request, Func<CancellationToken, Task> checkpoint,
        Func<QhyJobSnapshot, CancellationToken, Task> observe,
        Func<QhyJobSnapshot, QhyFrameRecord, CancellationToken, Task> analyze, CancellationToken token)
    {
        if (request.WideFieldStream is null || request.WideFieldStream.DeadlineUtc <= DateTimeOffset.UtcNow)
            return null;
        QhyServiceHealth health;
        try { health = await client.GetHealthAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or System.IO.IOException or InvalidOperationException or OperationCanceledException)
        { return null; } // No start request was dispatched; no physical stop is unknown.
        if (health.Capabilities?.Contains(QhyControlProtocol.WideFieldStreamCapability, StringComparer.Ordinal) != true)
            return null; // An old owner must not silently ignore the new request fields.
        var job = await client.StartOrAdoptAcquisitionAsync(request, token, checkpoint).ConfigureAwait(false);
        await observe(job, token).ConfigureAwait(false);
        var session = new QhyWideFieldStreamSession(client, request, job, checkpoint, observe, analyze, token);
        if (job.WideFieldStream != request.WideFieldStream)
        {
            await session.RetireOwnerAsync().ConfigureAwait(false);
            throw new InvalidOperationException("QHY_STREAM_CAPABILITY_MISMATCH: The existing owner did not attest the bounded request; its discovered job was joined and retired.");
        }
        session.consumer = session.ConsumeAsync();
        session.producer = session.ProduceAsync();
        return session;
    }

    private async Task ProduceAsync()
    {
        Guid? published = null;
        var renewAt = DateTimeOffset.UtcNow.AddSeconds(5);
        try
        {
            while (true)
            {
                await checkpoint(lifetime.Token).ConfigureAwait(false);
                var current = await client.GetJobAsync(JobId, lifetime.Token).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("QHY_STREAM_JOB_LOST: The existing owner lost its stream job.");
                if (current.ObservationRunId != request.ObservationRunId || current.ExpectedCameraStableId != Snapshot.ExpectedCameraStableId ||
                    current.WideFieldStream != request.WideFieldStream || current.ClientRequestId != request.ClientRequestId)
                    throw new InvalidOperationException("QHY_STREAM_OWNER_CHANGED: Stream identity or immutable request changed.");
                Volatile.Write(ref snapshot, current);
                await observe(current, lifetime.Token).ConfigureAwait(false);
                if (current.OperatorInterventionRequired || current.State is QhyJobState.Paused or QhyJobState.PausedNeedsAttention)
                    break; // Never renew/restart through an operator or owner attention pause.
                var newest = current.Frames.OrderBy(f => f.ExposureMidpointUtc).LastOrDefault();
                if (newest is not null && newest.FrameId != published)
                {
                    published = newest.FrameId;
                    queue.Writer.TryWrite((current, newest));
                }
                if (Terminal(current.State)) break;
                if (DateTimeOffset.UtcNow >= renewAt)
                {
                    await checkpoint(lifetime.Token).ConfigureAwait(false);
                    await client.RenewLeaseAsync(JobId, request.ControlLeaseSeconds, lifetime.Token).ConfigureAwait(false);
                    renewAt = DateTimeOffset.UtcNow.AddSeconds(5);
                }
                await Task.Delay(200, lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { terminalFailure = ex; }
        finally
        {
            queue.Writer.TryComplete();
            try { await RetireOwnerAsync().ConfigureAwait(false); }
            catch (Exception ex) { retirementFailure = ex; }
        }
    }

    private async Task ConsumeAsync()
    {
        try
        {
            await foreach (var (job, frame) in queue.Reader.ReadAllAsync(lifetime.Token).ConfigureAwait(false))
            {
                await analyze(job, frame, lifetime.Token).ConfigureAwait(false);
                Interlocked.Increment(ref analyzedFrames);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            analysisFailure = ex;
            lifetime.Cancel(); // A failed consumer cannot leave an orphan capture loop.
        }
    }

    private async Task RetireOwnerAsync()
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var current = await client.GetJobAsync(JobId, cleanup.Token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("QHY_STREAM_STOP_UNCONFIRMED: Missing owner job.");
        if (!Terminal(current.State)) await client.CancelAsync(JobId, cleanup.Token).ConfigureAwait(false);
        while (true)
        {
            current = await client.GetJobAsync(JobId, cleanup.Token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("QHY_STREAM_STOP_UNCONFIRMED: Missing terminal owner job.");
            Volatile.Write(ref snapshot, current);
            if (Terminal(current.State) && current.CompletedUtc is not null && current.ExecutionJoined) break;
            await Task.Delay(100, cleanup.Token).ConfigureAwait(false);
        }
        await observe(current, cleanup.Token).ConfigureAwait(false);
    }

    internal async Task<GateResult> StopAsync(CancellationToken token)
    {
        lifetime.Cancel();
        try
        {
            await Task.WhenAll(producer, consumer).WaitAsync(TimeSpan.FromSeconds(35), token).ConfigureAwait(false);
            return IsJoined && retirementFailure is null
                ? GateResult.Pass("QHY_STREAM_JOINED_STOP", "The existing owner job, native capture and latest-wins consumer are joined; no new exposure was started.")
                : GateResult.Unknown("QHY_STREAM_STOP_UNCONFIRMED", retirementFailure?.Message ?? "Stream owner join is unavailable.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        { return GateResult.Unknown("QHY_STREAM_STOP_UNCONFIRMED", ex.Message); }
    }

    private static bool Terminal(QhyJobState state) => state is QhyJobState.Completed or QhyJobState.Cancelled or QhyJobState.Faulted or QhyJobState.TakenOver;
}
