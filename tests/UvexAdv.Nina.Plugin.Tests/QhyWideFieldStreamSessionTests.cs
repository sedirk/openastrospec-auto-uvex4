using System.Net;
using System.Net.Http.Json;
using UvexAdv.Observatory;
using UvexAdv.Qhy.Core;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class QhyWideFieldStreamSessionTests
{
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
        var measured = new System.Collections.Concurrent.ConcurrentQueue<int>();
        var session = await QhyWideFieldStreamSession.StartAsync(client, Request(), _ => Task.CompletedTask,
            (_, _) => Task.CompletedTask, async (_, frame, token) =>
            {
                measured.Enqueue(frame.SequenceNumber);
                if (frame.SequenceNumber == 1) { entered.TrySetResult(); await release.Task.WaitAsync(token); }
            }, CancellationToken.None);
        Assert.NotNull(session);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        for (var i = 0; i < 100 && transport.FrameReads < 4; i++) await Task.Delay(20);
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
