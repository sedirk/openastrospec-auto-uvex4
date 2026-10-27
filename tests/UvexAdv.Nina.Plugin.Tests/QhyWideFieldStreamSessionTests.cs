using System.Net;
using System.Net.Http.Json;
using UvexAdv.Observatory;
using UvexAdv.Qhy.Core;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class QhyWideFieldStreamSessionTests
{
    [Fact]
    public async Task MissingContinuousPoseDoesNotDispatchSecondJobOrDiscardG3Frame()
    {
        using var transport = new OwnerTransport { ProduceFrames = true };
        using var client = new QhyServiceClient("http://127.0.0.1", transport);
        var session = await QhyWideFieldStreamSession.StartAsync(client, Request(), _ => Task.CompletedTask,
            (_, _) => Task.CompletedTask, (_, _, _) => Task.CompletedTask, CancellationToken.None);
        var dualCaptures = 0;
        var nativeSolves = 0;
        var existingG3 = new object();
        var optional = await G3SparseRecoveryWorkflow.TryAvailableBeforeSolveAsync<object>(
            _ => Task.CompletedTask, _ => Task.CompletedTask,
            _ => Task.FromResult<object?>(null),
            t => G3SparseRecoveryWorkflow.WithoutContinuousOwnerAsync(session is not null,
                _ => { dualCaptures++; return Task.FromException<object?>(new InvalidOperationException("409: already active")); }, t),
            _ => Task.FromResult<object?>(null), default);
        if (optional is null) { nativeSolves++; Assert.NotNull(existingG3); }
        Assert.Equal(0, dualCaptures); Assert.Equal(1, nativeSolves); Assert.Equal(1, transport.Starts);
        Assert.Equal(GateDisposition.Passed, (await session!.StopAsync(default)).Disposition);
    }

    [Fact]
    public async Task PrechargedMotionDefersAFrameButSameOwnerMeasuresNextArrivedFrames()
    {
        using var transport = new OwnerTransport { ProduceFrames = true };
        using var client = new QhyServiceClient("http://127.0.0.1", transport);
        var now = DateTimeOffset.UtcNow;
        var state = MotionState(now);
        var measured = new List<int>();
        var session = await QhyWideFieldStreamSession.StartAsync(client, Request(), _ => Task.CompletedTask,
            (_, _) => Task.CompletedTask, async (_, frame, token) =>
            {
                var phase = frame.SequenceNumber == 1 ? G3AcquisitionMotionPhase.OutboundIntent : G3AcquisitionMotionPhase.AwaitingFreshSolve;
                await RealObservationStageRunner.RunWideFieldKeyframeWhenStationaryAsync(state with { Phase = phase }, now,
                    _ => { measured.Add(frame.SequenceNumber); return Task.FromResult("measured"); }, token);
            }, CancellationToken.None);
        Assert.NotNull(session);
        for (var i = 0; i < 100 && !session.IsJoined; i++) await Task.Delay(20);
        Assert.True(session.IsJoined);
        Assert.Null(session.AnalysisFailure);
        Assert.Null(session.ProducerFailure);
        Assert.Equal(new[] { 2, 3, 4 }, measured);
        Assert.Equal(1, transport.Starts); Assert.Equal(0, transport.Cancels);
        Assert.Equal(1, state.CorrectionAttempts); // policy never resets/writes the immutable ledger
        Assert.Equal(GateDisposition.Passed, (await session.StopAsync(CancellationToken.None)).Disposition);
    }

    [Theory]
    [InlineData("return")] [InlineData("invalid")] [InlineData("clock")] [InlineData("reserve")] [InlineData("cancel")]
    public async Task BackgroundDeferralDoesNotWaiveIntegrityClockReserveOrCancellation(string scenario)
    {
        var now = DateTimeOffset.UtcNow;
        var state = MotionState(now) with { Phase = G3AcquisitionMotionPhase.AwaitingFreshSolve };
        using var stop = new CancellationTokenSource();
        if (scenario == "return") state = state with { Phase = G3AcquisitionMotionPhase.ReturnIntent };
        if (scenario == "invalid") state = state with { Phase = G3AcquisitionMotionPhase.OutboundIntent, RecoveryContextSha256 = "bad" };
        if (scenario == "clock") state = state with { Phase = G3AcquisitionMotionPhase.OutboundIntent, UpdatedUtc = now.AddSeconds(1) };
        if (scenario == "reserve") now = now.AddSeconds(280);
        if (scenario == "cancel") stop.Cancel();
        var called = false;
        Task<string?> Work() => RealObservationStageRunner.RunWideFieldKeyframeWhenStationaryAsync(state, now,
            _ => { called = true; return Task.FromResult("not allowed"); }, stop.Token);
        if (scenario == "return") Assert.Null(await Work());
        else if (scenario == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(Work);
        else await Assert.ThrowsAsync<G3ReturnTimeReserveException>(Work);
        Assert.False(called);
    }

    private static G3AcquisitionMotionState MotionState(DateTimeOffset now) => new(
        G3AcquisitionMotionState.CurrentSchemaVersion, G3AcquisitionMotionState.CurrentTangentProjectionId,
        "finite-stream-run", Guid.NewGuid().ToString("N"), new string('A',64), new string('B',64), new string('C',64),
        G3AcquisitionMotionKind.LocalSearch, G3AcquisitionMotionPhase.SettledBudgetLedger, "pierEast", "J2000",
        10,20,10,20,10,20,0,0,5,30,100,500,20,2,10,300,5,1,now,now,now,"finite-evidence","finite test");

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task UnsupportedOrUnavailableHealthDoesNotDispatchAnyJob(bool unavailable)
    {
        using var transport = new OwnerTransport { Advertise = false, HealthUnavailable = unavailable };
        using var client = new QhyServiceClient("http://127.0.0.1", transport);
        var session = await QhyWideFieldStreamSession.StartAsync(client, Request(), _ => Task.CompletedTask,
            (_, _) => Task.CompletedTask, (_, _, _) => Task.CompletedTask, CancellationToken.None);
        Assert.Null(session); Assert.Equal(0, transport.Starts);
    }

    [Fact]
    public async Task OriginalExecutionGateIsRetainedWhileActualJoinedStopIsConfirmed()
    {
        using var transport = new OwnerTransport();
        using var client = new QhyServiceClient("http://127.0.0.1", transport);
        var calls = 0;
        var original = new PhysicalActionGateException(GateResult.Unknown("WEATHER_STATE_UNKNOWN", "finite unsafe readback"));
        var session = await QhyWideFieldStreamSession.StartAsync(client, Request(), _ =>
        {
            if (Interlocked.Increment(ref calls) > 1) throw original;
            return Task.CompletedTask;
        }, (_, _) => Task.CompletedTask, (_, _, _) => Task.CompletedTask, CancellationToken.None);
        Assert.NotNull(session);
        var stopped = await session.StopAsync(CancellationToken.None);
        Assert.Equal(GateDisposition.Passed, stopped.Disposition);
        Assert.Equal("QHY_STREAM_JOINED_STOP", stopped.Code);
        Assert.Same(original, session.ProducerFailure);
        Assert.True(session.IsJoined); Assert.Equal(1, transport.Cancels);
    }

    [Fact]
    public async Task LatestWinsHasOnePendingAnalysisAndRetiresTheSameOwner()
    {
        using var transport = new OwnerTransport { ProduceFrames = true };
        using var client = new QhyServiceClient("http://127.0.0.1", transport);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminalObservations = 0;
        var measured = new System.Collections.Concurrent.ConcurrentQueue<int>();
        var session = await QhyWideFieldStreamSession.StartAsync(client, Request(), _ => Task.CompletedTask,
            (job, _) =>
            {
                // The first terminal observation precedes publication to the queue;
                // retirement observes it again after the producer has closed that queue.
                // A transport read count alone can release analysis before frame 4 is published.
                if (job.State == QhyJobState.Completed && Interlocked.Increment(ref terminalObservations) == 2)
                    retired.TrySetResult();
                return Task.CompletedTask;
            }, async (_, frame, token) =>
            {
                measured.Enqueue(frame.SequenceNumber);
                if (frame.SequenceNumber == 1) { entered.TrySetResult(); await release.Task.WaitAsync(token); }
            }, CancellationToken.None);
        Assert.NotNull(session);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await retired.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(transport.FrameReads >= 4);
        release.TrySetResult();
        for (var i = 0; i < 100 && !session.IsJoined; i++) await Task.Delay(20);
        Assert.Equal(new[] { 1, 4 }, measured.ToArray());
        Assert.Equal(GateDisposition.Passed, (await session.StopAsync(CancellationToken.None)).Disposition);
        Assert.Equal(1, transport.Starts);
    }

    [Fact]
    public async Task LostPhysicalJobCannotBecomeConfirmedStop()
    {
        using var transport = new OwnerTransport { LoseJob = true };
        using var client = new QhyServiceClient("http://127.0.0.1", transport);
        var session = await QhyWideFieldStreamSession.StartAsync(client, Request(), _ => Task.CompletedTask,
            (_, _) => Task.CompletedTask, (_, _, _) => Task.CompletedTask, CancellationToken.None);
        Assert.NotNull(session);
        Assert.Equal("QHY_STREAM_STOP_UNCONFIRMED", (await session.StopAsync(CancellationToken.None)).Code);
        Assert.False(session.IsJoined);
    }

    private static AcquisitionJobRequest Request() => new("finite-stream-run", "field", [1], 10, 20,
        MaximumAttempts: 1, ClientRequestId: "finite-stream-request", ControlLeaseSeconds: 15,
        WideFieldStream: new(DateTimeOffset.UtcNow.AddSeconds(30), 4));

    private sealed class OwnerTransport : HttpMessageHandler
    {
        public bool Advertise { get; init; } = true;
        public bool HealthUnavailable { get; init; }
        public bool ProduceFrames { get; init; }
        public bool LoseJob { get; init; }
        public int Starts; public int Cancels; public int FrameReads;
        private QhyJobSnapshot? job;
        private readonly string owner = new('x', 43);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken token)
        {
            var path = message.RequestUri!.AbsolutePath;
            if (path == "/api/v1/health")
            {
                if (HealthUnavailable) throw new HttpRequestException("finite health unavailable");
                var proof = QhyServiceConfigurationProof.Create(true, "simulator", "QHYminiCam8M", "finite-camera", null, 1);
                return Reply(new QhyServiceHealth("test-owner", "ok", true, proof, DateTimeOffset.UtcNow,
                    Capabilities: Advertise ? [QhyControlProtocol.WideFieldStreamCapability] : null));
            }
            if (path == "/api/v1/jobs/acquisition")
            {
                var r = (await message.Content!.ReadFromJsonAsync<AcquisitionJobRequest>(cancellationToken: token))!;
                Starts++;
                job = new(Guid.NewGuid(), r.ObservationRunId, QhyJobKind.Acquisition, QhyJobState.Running,
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, r.RequestedTarget, "finite-camera", null, null,
                    [], [], "not-a-raw-file-manifest", ClientRequestId: r.ClientRequestId,
                    LeaseExpiresUtc: DateTimeOffset.UtcNow.AddSeconds(15), ControlLeaseSeconds: 15, WideFieldStream: r.WideFieldStream);
                var response = Reply(job);
                response.Headers.Add(QhyControlProtocol.OwnerTokenHeaderName, owner);
                response.Headers.Add(QhyControlProtocol.LeaseExpiresUtcHeaderName, job.LeaseExpiresUtc!.Value.ToString("O"));
                return response;
            }
            if (LoseJob) return new(HttpStatusCode.NotFound);
            if (path.EndsWith("/cancel", StringComparison.Ordinal))
            {
                Cancels++; job = job! with { State = QhyJobState.Cancelled, CompletedUtc = DateTimeOffset.UtcNow, ExecutionJoined = true };
            }
            else if (ProduceFrames && message.Method == HttpMethod.Get && job!.State == QhyJobState.Running)
            {
                var n = Math.Min(4, Interlocked.Increment(ref FrameReads));
                var now = DateTimeOffset.UtcNow;
                var metrics = new QhyFrameMetrics(0, 100, 50, 50, 1, 50, 50, 50, 0, 0, 0, null, null, null, null, []);
                var frame = new QhyFrameRecord(new Guid(n, 0, 0, new byte[8]), n, "ACQUISITION-WIDE-STREAM", "finite-" + n + ".fits",
                    "preview.png", new string((char)('A' + n), 64), now.AddSeconds(-2), now.AddSeconds(-1), now, new(1, 10, 20), metrics);
                job = job with { Frames = [frame], TotalFrameCount = n, State = n == 4 ? QhyJobState.Completed : QhyJobState.Running,
                    CompletedUtc = n == 4 ? now : null, ExecutionJoined = n == 4 };
            }
            return Reply(job!);
        }
        private static HttpResponseMessage Reply<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    }
}
