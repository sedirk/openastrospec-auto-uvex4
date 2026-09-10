using System.Net;
using System.Net.Http.Json;
using UvexAdv.Qhy.Core;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class QhyAcquisitionPriorityTests
{
    [Fact]
    public async Task AcquisitionWaitsForSavedFramePauseAndCheckedTerminalWithoutChangingOwner()
    {
        using var transport = new PriorityTransport();
        using var client = new QhyServiceClient("http://127.0.0.1", transport);
        var phases = new List<string>();
        client.AcquisitionPriorityProgress = phases.Add;
        var photometry = await client.StartOrAdoptPhotometryAsync(PhotoRequest(), CancellationToken.None);
        var acquisition = await client.StartOrAdoptAcquisitionAsync(AcquisitionRequest(), CancellationToken.None);
        Assert.Equal(QhyJobKind.Acquisition, acquisition.Kind);
        Assert.Equal(new[] { "photometry", "pause", "cancel", "acquisition" }, transport.Actions);
        Assert.Equal(new[] { "yield-requested", "yield-confirmed" }, phases);
        Assert.Equal(1, transport.PhotometryStarts);
        Assert.True(transport.TerminalReadBack);
        var retired = (await client.GetJobAsync(photometry.Id, CancellationToken.None))!;
        Assert.True(QhyServiceClient.IsAcquisitionPriorityYield(retired));
        Assert.Equal("witness-1", retired.YieldedToAcquisitionRequestId);
        Assert.Equal(1, retired.TotalFrameCount);
        Assert.False(client.HasOwnerSession(retired.Id));
        Assert.True(client.HasOwnerSession(acquisition.Id));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ManualPauseOrAnotherRunCannotBePreempted(bool manual, bool otherRun)
    {
        using var transport = new PriorityTransport { Manual = manual, OtherRun = otherRun };
        using var client = new QhyServiceClient("http://127.0.0.1", transport);
        await client.StartPhotometryAsync(PhotoRequest(), CancellationToken.None);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.StartAcquisitionAsync(AcquisitionRequest(), CancellationToken.None));
        Assert.Contains(manual ? "PHOTOMETRY_OPERATOR_PAUSED" : "QHY_PRIORITY_OTHER_RUN", error.Message);
        Assert.Equal(new[] { "photometry" }, transport.Actions);
    }

    [Fact]
    public async Task ManualInterventionDuringFrameBoundaryWaitWinsOverPendingAcquisition()
    {
        using var transport = new PriorityTransport { ManualDuringPause = true };
        using var client = new QhyServiceClient("http://127.0.0.1", transport);
        await client.StartPhotometryAsync(PhotoRequest(), CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.StartAcquisitionAsync(AcquisitionRequest(), CancellationToken.None));
        Assert.Equal(new[] { "photometry", "pause" }, transport.Actions);
        Assert.Equal(QhyJobState.Paused, transport.Photo.State);
    }

    [Fact]
    public async Task MissingReleaseTimesOutWithoutAcquisitionOrRepeatedPhotoStart()
    {
        using var transport = new PriorityTransport { NeverPauses = true };
        using var client = new QhyServiceClient("http://127.0.0.1", transport)
            { AcquisitionPriorityTimeout = TimeSpan.FromMilliseconds(500) };
        await client.StartPhotometryAsync(PhotoRequest(), CancellationToken.None);
        var error = await Assert.ThrowsAsync<TimeoutException>(() => client.StartOrAdoptAcquisitionAsync(AcquisitionRequest(), CancellationToken.None));
        Assert.Contains("QHY_PRIORITY_RELEASE_TIMEOUT", error.Message);
        Assert.Equal(new[] { "photometry", "pause" }, transport.Actions);
    }

    [Fact]
    public async Task NewPhotometryCannotRaceIntoTheCameraDuringPriorityHandoff()
    {
        using var transport = new PriorityTransport { HoldPause = true };
        using var client = new QhyServiceClient("http://127.0.0.1", transport);
        await client.StartPhotometryAsync(PhotoRequest(), CancellationToken.None);
        var acquisition = client.StartAcquisitionAsync(AcquisitionRequest(), CancellationToken.None);
        await transport.PauseEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var photo = client.StartPhotometryAsync(PhotoRequest() with { ClientRequestId = "photo-2" }, CancellationToken.None);
        Assert.False(photo.IsCompleted);
        transport.ReleasePause.TrySetResult();
        await acquisition;
        await Assert.ThrowsAsync<HttpRequestException>(() => photo);
        Assert.Equal(new[] { "photometry", "pause", "cancel", "acquisition", "photometry-busy" }, transport.Actions);
    }

    [Fact]
    public async Task ImmediateSafetyGateRunsAfterHandoffAndCanPreventTheWitness()
    {
        using var transport = new PriorityTransport();
        using var client = new QhyServiceClient("http://127.0.0.1", transport);
        await client.StartPhotometryAsync(PhotoRequest(), CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.StartOrAdoptAcquisitionAsync(
            AcquisitionRequest(), CancellationToken.None, _ =>
            {
                Assert.True(transport.TerminalReadBack);
                throw new InvalidOperationException("fixture-roof-no-longer-open");
            }));
        Assert.Equal(new[] { "photometry", "pause", "cancel" }, transport.Actions);
    }

    [Theory]
    [InlineData(QhyJobState.Cancelled, false, "witness-1", true)]
    [InlineData(QhyJobState.Cancelled, true, "witness-1", false)]
    [InlineData(QhyJobState.Cancelled, false, null, false)]
    [InlineData(QhyJobState.Faulted, false, "witness-1", false)]
    [InlineData(QhyJobState.TakenOver, false, "witness-1", false)]
    public void OnlyExplicitPriorityRetirementCanAutomaticallyContinue(QhyJobState state, bool manual, string? witness, bool mayContinue)
    {
        using var transport = new PriorityTransport();
        var job = transport.Photo with { State = state, OperatorInterventionRequired = manual, YieldedToAcquisitionRequestId = witness };
        Assert.Equal(mayContinue, QhyServiceClient.IsAcquisitionPriorityYield(job));
    }

    private static PhotometryJobRequest PhotoRequest() => new("run", "target", 5, 10, 0, 100, 5, ClientRequestId: "photo-1");
    private static AcquisitionJobRequest AcquisitionRequest() => new("run", "target", [1], 10, 0, ClientRequestId: "witness-1");

    private sealed class PriorityTransport : HttpMessageHandler
    {
        private readonly string owner = new('a', 43);
        public QhyJobSnapshot Photo = new(Guid.NewGuid(), "run", QhyJobKind.Photometry, QhyJobState.Running,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, "target", "fixture-camera", null, null, [], [], "manifest",
            ClientRequestId: "photo-1", LeaseExpiresUtc: DateTimeOffset.UtcNow.AddMinutes(2));
        public bool Manual, OtherRun, ManualDuringPause, NeverPauses, HoldPause, TerminalReadBack;
        public int PhotometryStarts;
        public List<string> Actions = [];
        public TaskCompletionSource PauseEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleasePause = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool acquisitionStarted;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path == "/api/v1/jobs/photometry")
            {
                if (acquisitionStarted)
                {
                    Actions.Add("photometry-busy");
                    return new(HttpStatusCode.Conflict) { Content = JsonContent.Create(new { error = "acquisition owns camera" }) };
                }
                Actions.Add("photometry"); PhotometryStarts++;
                Photo = Photo with { ObservationRunId = OtherRun ? "another-run" : "run", OperatorInterventionRequired = Manual };
                return Start(Photo);
            }
            if (request.Method == HttpMethod.Post && path == "/api/v1/jobs/acquisition")
            {
                Assert.True(TerminalReadBack);
                Assert.Equal(QhyJobState.Cancelled, Photo.State);
                Actions.Add("acquisition"); acquisitionStarted = true;
                return Start(Photo with { Id = Guid.NewGuid(), Kind = QhyJobKind.Acquisition, State = QhyJobState.Running,
                    ClientRequestId = "witness-1", YieldedToAcquisitionRequestId = null, TotalFrameCount = 0 });
            }
            Assert.StartsWith($"/api/v1/jobs/{Photo.Id:D}", path);
            if (request.Method == HttpMethod.Get)
            {
                if (Photo.State == QhyJobState.Pausing && !NeverPauses)
                    Photo = Photo with { State = QhyJobState.Paused, TotalFrameCount = 1, OperatorInterventionRequired = ManualDuringPause };
                if (Photo.State == QhyJobState.Cancelling)
                {
                    Photo = Photo with { State = QhyJobState.Cancelled };
                    TerminalReadBack = true;
                }
                return Json(Photo);
            }
            var control = (await request.Content!.ReadFromJsonAsync<QhyOwnerControlRequest>(cancellationToken: token))!;
            Assert.Equal(owner, control.OwnerToken);
            if (path.EndsWith("/pause", StringComparison.Ordinal))
            {
                Actions.Add("pause"); PauseEntered.TrySetResult();
                if (HoldPause) await ReleasePause.Task.WaitAsync(token);
                Photo = Photo with { State = QhyJobState.Pausing };
            }
            else if (path.EndsWith("/cancel", StringComparison.Ordinal))
            {
                Assert.Equal(QhyJobState.Paused, Photo.State);
                Assert.Equal("witness-1", control.YieldToAcquisitionRequestId);
                Actions.Add("cancel");
                Photo = Photo with { State = QhyJobState.Cancelling, YieldedToAcquisitionRequestId = control.YieldToAcquisitionRequestId };
            }
            else throw new InvalidOperationException(path);
            return Json(Photo);
        }
        private HttpResponseMessage Start(QhyJobSnapshot job)
        {
            var response = Json(job);
            response.Headers.Add(QhyControlProtocol.OwnerTokenHeaderName, owner);
            response.Headers.Add(QhyControlProtocol.LeaseExpiresUtcHeaderName, job.LeaseExpiresUtc!.Value.ToString("O"));
            return response;
        }
        private static HttpResponseMessage Json(QhyJobSnapshot job) => new(HttpStatusCode.OK) { Content = JsonContent.Create(job) };
    }
}
