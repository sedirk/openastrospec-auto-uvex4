using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using UvexAdv.Qhy.Core;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class QhyServiceClientTests
{
    [Fact]
    public async Task StartStoresHeaderTokenOnlyInMemoryAndPauseSendsOwnerDto()
    {
        var id = Guid.NewGuid();
        var expiry = DateTimeOffset.UtcNow.AddMinutes(2);
        var ownerToken = new string('s', 43);
        string? submittedToken = null;
        var handler = new DelegateHandler(async request =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/api/v1/jobs/acquisition")
            {
                return StartResponse(Snapshot(id, QhyJobState.Running, expiry), ownerToken, expiry);
            }
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == $"/api/v1/jobs/{id:D}/pause")
            {
                using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                submittedToken = document.RootElement.GetProperty("ownerToken").GetString();
                return JsonResponse(HttpStatusCode.OK, Snapshot(id, QhyJobState.Paused, expiry));
            }
            throw new InvalidOperationException($"Unexpected request {request.Method} {request.RequestUri}.");
        });

        using var client = new QhyServiceClient("http://127.0.0.1:18991", handler);
        var started = await client.StartOrAdoptAcquisitionAsync(Request("owner-header"), CancellationToken.None);
        Assert.Equal(id, started.Id);
        Assert.True(client.HasOwnerSession(id));

        var paused = await client.PauseAsync(id, CancellationToken.None);
        Assert.Equal(QhyJobState.Paused, paused.State);
        Assert.Equal(ownerToken, submittedToken);
    }

    [Fact]
    public async Task AcceptedStartWithoutTokenIsFoundByIdempotencyKeyAndForcedSafe()
    {
        var id = Guid.NewGuid();
        var expiry = DateTimeOffset.UtcNow.AddMinutes(2);
        var startPosts = 0;
        var lookups = 0;
        var takeovers = 0;
        var handler = new DelegateHandler(request =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/api/v1/jobs/acquisition")
            {
                startPosts++;
                return Task.FromResult(JsonResponse(HttpStatusCode.Accepted, Snapshot(id, QhyJobState.Running, expiry)));
            }
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/api/v1/jobs/lookup")
            {
                lookups++;
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, Snapshot(id, QhyJobState.Running, expiry)));
            }
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == $"/api/v1/jobs/{id:D}/takeover")
            {
                takeovers++;
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, Snapshot(id, QhyJobState.TakenOver, expiry)));
            }
            throw new InvalidOperationException($"Unexpected request {request.Method} {request.RequestUri}.");
        });

        using var client = new QhyServiceClient("http://127.0.0.1:18991", handler);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.StartOrAdoptAcquisitionAsync(Request("missing-token"), CancellationToken.None));

        Assert.Contains("checked safe terminal state", exception.Message, StringComparison.Ordinal);
        Assert.Equal(3, startPosts);
        Assert.Equal(1, lookups);
        Assert.Equal(1, takeovers);
        Assert.False(client.HasOwnerSession(id));
    }

    [Fact]
    public async Task ExplicitConflictIsNotRetriedAsAmbiguousTransportFailure()
    {
        var startPosts = 0;
        var handler = new DelegateHandler(request =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/api/v1/jobs/acquisition")
            {
                startPosts++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)
                {
                    Content = JsonContent.Create(new { detail = "fingerprint mismatch" }),
                });
            }
            throw new InvalidOperationException($"Unexpected request {request.Method} {request.RequestUri}.");
        });

        using var client = new QhyServiceClient("http://127.0.0.1:18991", handler);
        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.StartOrAdoptAcquisitionAsync(Request("conflict"), CancellationToken.None));

        Assert.Equal(HttpStatusCode.Conflict, exception.StatusCode);
        Assert.Equal(1, startPosts);
    }

    [Theory]
    [InlineData(QhyJobKind.Photometry, true, 1)]
    [InlineData(QhyJobKind.Photometry, false, 2)]
    [InlineData(QhyJobKind.Acquisition, true, 2)]
    public async Task FirstFrameMayWarnOnlyForExplicitlyOptionalPhotometry(
        QhyJobKind kind, bool allowWarning, int expectedPolls)
    {
        var id = Guid.NewGuid();
        var frame = Frame();
        var polls = 0;
        var handler = new DelegateHandler(_ => Task.FromResult(JsonResponse(HttpStatusCode.OK,
            Snapshot(id, QhyJobState.Running, DateTimeOffset.UtcNow.AddMinutes(2)) with
            {
                Kind = kind,
                Frames = [frame],
                LastEvaluatedFrameId = frame.FrameId,
                LastFramePassedQualityGate = ++polls > 1,
            })));
        using var client = new QhyServiceClient("http://127.0.0.1:18991", handler);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await client.WaitForFirstFrameOrTerminalAsync(id, null, timeout.Token, allowWarning);
        Assert.Equal(expectedPolls, polls);
        Assert.Equal(expectedPolls > 1, result.LastFramePassedQualityGate);
        Assert.Equal(0, result.TotalAcceptedFrameCount);
        Assert.Contains("STAR_DETECTION_CAPPED", result.Frames.Single().Metrics.QualityFlags);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OptionalPhotometryStillWaitsForEvaluationOfTheExactSavedFrame(bool mismatchedFrame)
    {
        var id = Guid.NewGuid();
        var frame = Frame();
        var polls = 0;
        var handler = new DelegateHandler(_ =>
        {
            polls++;
            return Task.FromResult(JsonResponse(HttpStatusCode.OK,
                Snapshot(id, QhyJobState.Running, DateTimeOffset.UtcNow.AddMinutes(2)) with
                {
                    Kind = QhyJobKind.Photometry,
                    Frames = [frame],
                    LastEvaluatedFrameId = polls == 1 && mismatchedFrame ? Guid.NewGuid() : frame.FrameId,
                    LastFramePassedQualityGate = polls == 1 && !mismatchedFrame ? null : false,
                }));
        });
        using var client = new QhyServiceClient("http://127.0.0.1:18991", handler);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await client.WaitForFirstFrameOrTerminalAsync(id, null, timeout.Token, allowPhotometryQualityWarning: true);
        Assert.Equal(2, polls);
        Assert.False(result.LastFramePassedQualityGate);
    }

    private static QhyFrameRecord Frame() => new(
        Guid.NewGuid(), 1, "PHOTOMETRY-R", "raw.fits", "preview.png", new string('a', 64),
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
        new QhyFrameSettings(5, 20, 20),
        new QhyFrameMetrics(0, 100, 10, 10, 1, 20, 30, 40, 0, 0, 500, 2.8, 0.05, 1600, 1,
            ["STAR_DETECTION_CAPPED"]));

    private static AcquisitionJobRequest Request(string requestId) => new(
        "plugin-owner-test",
        "target",
        [0.1],
        10,
        256,
        ClientRequestId: requestId,
        ControlLeaseSeconds: 120);

    private static QhyJobSnapshot Snapshot(Guid id, QhyJobState state, DateTimeOffset expiry) => new(
        id,
        "plugin-owner-test",
        QhyJobKind.Acquisition,
        state,
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow,
        state is QhyJobState.Completed or QhyJobState.Cancelled or QhyJobState.Faulted or QhyJobState.TakenOver
            ? DateTimeOffset.UtcNow
            : null,
        "target",
        "QHY-TEST",
        null,
        null,
        [],
        [],
        "manifest.json",
        ClientRequestId: "request",
        LeaseExpiresUtc: expiry,
        ControlLeaseSeconds: 120);

    private static HttpResponseMessage StartResponse(
        QhyJobSnapshot snapshot,
        string ownerToken,
        DateTimeOffset expiry)
    {
        var response = JsonResponse(HttpStatusCode.Accepted, snapshot);
        response.Headers.TryAddWithoutValidation(QhyControlProtocol.OwnerTokenHeaderName, ownerToken);
        response.Headers.TryAddWithoutValidation(QhyControlProtocol.LeaseExpiresUtcHeaderName, expiry.ToString("O"));
        response.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoStore = true };
        return response;
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, QhyJobSnapshot snapshot) => new(status)
    {
        Content = JsonContent.Create(snapshot),
    };

    private sealed class DelegateHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request);
    }
}
