using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using UvexAdv.Core;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class UvexServiceClientTests
{
    [Fact]
    public async Task LeaseScopedSlitCommandWaitsAndVerifiesReadback()
    {
        var token = new string('l', 32);
        var operationId = Guid.NewGuid();
        var startedUtc = DateTimeOffset.UtcNow;
        var calls = new List<string>();
        bool? submittedEnabled = null;
        string? submittedLease = null;
        var handler = new DelegateHandler(async request =>
        {
            calls.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            if (request.Method == HttpMethod.Post && request.RequestUri.AbsolutePath == "/api/v1/leases")
            {
                return JsonResponse(HttpStatusCode.OK, new ServiceLease(token, "test", DateTimeOffset.UtcNow.AddMinutes(1)));
            }
            if (request.Method == HttpMethod.Post && request.RequestUri.AbsolutePath == "/api/v1/slit/illumination")
            {
                submittedLease = request.Headers.GetValues("X-Uvex-Lease").Single();
                using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                submittedEnabled = document.RootElement.GetProperty("enabled").GetBoolean();
                return JsonResponse(
                    HttpStatusCode.Accepted,
                    new ServiceOperation(operationId, "slit.illumination", "Pending", startedUtc, null, null));
            }
            if (request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath == $"/api/v1/operations/{operationId:D}")
            {
                return JsonResponse(
                    HttpStatusCode.OK,
                    new ServiceOperation(operationId, "slit.illumination", "Succeeded", startedUtc, DateTimeOffset.UtcNow, null));
            }
            if (request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath == "/api/v1/device")
            {
                return JsonResponse(HttpStatusCode.OK, new UvexDeviceStatus
                {
                    ConnectionState = DeviceConnectionState.Ready,
                    PositionKnown = true,
                    PositionTrust = UvexPositionTrust.Live,
                    SlitIlluminationLedState = UvexOutputState.On,
                    SlitIlluminationLedCommandedUtc = startedUtc.AddMilliseconds(1),
                    TimestampUtc = startedUtc.AddMilliseconds(2),
                });
            }
            if (request.Method == HttpMethod.Delete && request.RequestUri.AbsolutePath == $"/api/v1/leases/{token}")
            {
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            throw new InvalidOperationException($"Unexpected request {request.Method} {request.RequestUri}.");
        });

        using var client = new UvexServiceClient("http://127.0.0.1:47844", handler);
        await using (var lease = await client.AcquireLeaseAsync("test", CancellationToken.None))
        {
            var status = await lease.SetSlitIlluminationAsync(true, CancellationToken.None);
            Assert.Equal(UvexOutputState.On, status.SlitIlluminationLedState);
        }

        Assert.True(submittedEnabled);
        Assert.Equal(token, submittedLease);
        Assert.True(calls.IndexOf($"GET /api/v1/operations/{operationId:D}") < calls.FindLastIndex(call => call == "GET /api/v1/device"));
        Assert.Equal($"DELETE /api/v1/leases/{token}", calls[^1]);
    }

    [Fact]
    public async Task CompletedSlitCommandWithMismatchedReadbackFailsClosed()
    {
        var token = new string('m', 32);
        var operationId = Guid.NewGuid();
        var startedUtc = DateTimeOffset.UtcNow;
        var handler = new DelegateHandler(request =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/api/v1/leases")
            {
                return Task.FromResult(JsonResponse(
                    HttpStatusCode.OK,
                    new ServiceLease(token, "test", DateTimeOffset.UtcNow.AddMinutes(1))));
            }
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/api/v1/slit/illumination")
            {
                return Task.FromResult(JsonResponse(
                    HttpStatusCode.Accepted,
                    new ServiceOperation(operationId, "slit.illumination", "Pending", startedUtc, null, null)));
            }
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == $"/api/v1/operations/{operationId:D}")
            {
                return Task.FromResult(JsonResponse(
                    HttpStatusCode.OK,
                    new ServiceOperation(operationId, "slit.illumination", "Succeeded", startedUtc, DateTimeOffset.UtcNow, null)));
            }
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/api/v1/device")
            {
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, new UvexDeviceStatus
                {
                    ConnectionState = DeviceConnectionState.Ready,
                    PositionKnown = true,
                    PositionTrust = UvexPositionTrust.Live,
                    SlitIlluminationLedState = UvexOutputState.On,
                    SlitIlluminationLedCommandedUtc = startedUtc.AddMilliseconds(1),
                }));
            }
            if (request.Method == HttpMethod.Delete && request.RequestUri!.AbsolutePath == $"/api/v1/leases/{token}")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }
            throw new InvalidOperationException($"Unexpected request {request.Method} {request.RequestUri}.");
        });

        using var client = new UvexServiceClient("http://127.0.0.1:47844", handler);
        await using var lease = await client.AcquireLeaseAsync("test", CancellationToken.None);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => lease.SetSlitIlluminationAsync(false, CancellationToken.None));

        Assert.Contains("readback is On; expected Off", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManualControlReleasesReconnectsAndVerifiesMotionReadback()
    {
        var token = new string('r', 32);
        var operations = new Dictionary<Guid, string>();
        var calls = new List<string>();
        var state = DeviceConnectionState.Ready;
        var positionKnown = true;
        var slitPosition = 2;
        var focusPosition = 12_500;
        var requestedSlit = slitPosition;
        var requestedFocusDelta = 0;
        var handler = new DelegateHandler(async request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            calls.Add($"{request.Method} {path}");
            if (request.Method == HttpMethod.Post && path == "/api/v1/leases")
            {
                return JsonResponse(HttpStatusCode.OK, new ServiceLease(token, "manual", DateTimeOffset.UtcNow.AddMinutes(1)));
            }
            if (request.Method == HttpMethod.Post && path is
                "/api/v1/device/maintenance/enter" or
                "/api/v1/device/maintenance/exit" or
                "/api/v1/device/disconnect" or
                "/api/v1/device/connect")
            {
                var id = Guid.NewGuid();
                operations[id] = path switch
                {
                    "/api/v1/device/maintenance/enter" => "enter",
                    "/api/v1/device/maintenance/exit" => "exit",
                    "/api/v1/device/disconnect" => "disconnect",
                    _ => "connect",
                };
                return JsonResponse(HttpStatusCode.Accepted, new ServiceOperation(id, operations[id], "Pending", DateTimeOffset.UtcNow, null, null));
            }
            if (request.Method == HttpMethod.Post && path == "/api/v1/slit/select")
            {
                using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                requestedSlit = json.RootElement.GetProperty("position").GetInt32();
                var id = Guid.NewGuid();
                operations[id] = "slit";
                return JsonResponse(HttpStatusCode.Accepted, new ServiceOperation(id, "slit", "Pending", DateTimeOffset.UtcNow, null, null));
            }
            if (request.Method == HttpMethod.Post && path == "/api/v1/focus/move")
            {
                using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                requestedFocusDelta = json.RootElement.GetProperty("deltaSteps").GetInt32();
                var id = Guid.NewGuid();
                operations[id] = "focus";
                return JsonResponse(HttpStatusCode.Accepted, new ServiceOperation(id, "focus", "Pending", DateTimeOffset.UtcNow, null, null));
            }
            if (request.Method == HttpMethod.Get && path.StartsWith("/api/v1/operations/", StringComparison.Ordinal))
            {
                var id = Guid.Parse(path[(path.LastIndexOf('/') + 1)..]);
                switch (operations[id])
                {
                    case "enter": state = DeviceConnectionState.Maintenance; positionKnown = false; break;
                    case "exit": state = DeviceConnectionState.Ready; positionKnown = true; break;
                    case "disconnect": state = DeviceConnectionState.Disconnected; positionKnown = false; break;
                    case "connect": state = DeviceConnectionState.Ready; positionKnown = true; break;
                    case "slit": slitPosition = requestedSlit; break;
                    case "focus": focusPosition += requestedFocusDelta; break;
                }
                return JsonResponse(HttpStatusCode.OK, new ServiceOperation(id, operations[id], "Succeeded", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null));
            }
            if (request.Method == HttpMethod.Get && path == "/api/v1/device")
            {
                return JsonResponse(HttpStatusCode.OK, new UvexDeviceStatus
                {
                    ConnectionState = state,
                    PositionKnown = positionKnown,
                    PositionTrust = positionKnown ? UvexPositionTrust.Live : UvexPositionTrust.LastKnown,
                    SlitPosition = slitPosition,
                    FocusPositionSteps = focusPosition,
                });
            }
            if (request.Method == HttpMethod.Delete && path == $"/api/v1/leases/{token}")
            {
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            throw new InvalidOperationException($"Unexpected request {request.Method} {request.RequestUri}.");
        });

        using var client = new UvexServiceClient("http://127.0.0.1:47844", handler);
        await using (var lease = await client.AcquireLeaseAsync("manual", CancellationToken.None))
        {
            var released = await lease.ReleaseComPortAndVerifyAsync(CancellationToken.None);
            Assert.Equal(DeviceConnectionState.Maintenance, released.ConnectionState);

            var connected = await lease.ConnectAndVerifyAsync(CancellationToken.None);
            Assert.Equal(DeviceConnectionState.Ready, connected.ConnectionState);
            Assert.True(connected.PositionKnown);

            var slit = await lease.SelectSlitAndVerifyAsync(4, CancellationToken.None);
            Assert.Equal(4, slit.SlitPosition);

            var focus = await lease.MoveFocusAndVerifyAsync(-50, CancellationToken.None);
            Assert.Equal(12_450, focus.FocusPositionSteps);

            var disconnected = await lease.DisconnectAndVerifyAsync(CancellationToken.None);
            Assert.Equal(DeviceConnectionState.Disconnected, disconnected.ConnectionState);
            Assert.False(disconnected.PositionKnown);

            var notConnected = await Assert.ThrowsAsync<InvalidOperationException>(
                () => lease.SelectSlitAndVerifyAsync(3, CancellationToken.None));
            Assert.Contains("Click Connect", notConnected.Message, StringComparison.Ordinal);

            var reconnected = await lease.ConnectAndVerifyAsync(CancellationToken.None);
            Assert.Equal(DeviceConnectionState.Ready, reconnected.ConnectionState);
            Assert.True(reconnected.PositionKnown);
        }

        Assert.Contains("POST /api/v1/device/maintenance/enter", calls);
        Assert.Contains("POST /api/v1/device/maintenance/exit", calls);
        Assert.Contains("POST /api/v1/slit/select", calls);
        Assert.Contains("POST /api/v1/focus/move", calls);
        Assert.Contains("POST /api/v1/device/disconnect", calls);
        Assert.Contains("POST /api/v1/device/connect", calls);
    }

    private static HttpResponseMessage JsonResponse<T>(HttpStatusCode status, T value) => new(status)
    {
        Content = JsonContent.Create(value),
    };

    private sealed class DelegateHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request);
    }
}
