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
        Assert.True(calls.IndexOf($"GET /api/v1/operations/{operationId:D}") < calls.IndexOf("GET /api/v1/device"));
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
