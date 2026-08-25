using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using UvexAdv.Core;

namespace UvexAdv.Service.Tests;

public sealed class ServiceApiTests : IClassFixture<SimulatorWebApplicationFactory>
{
    private readonly SimulatorWebApplicationFactory factory;

    public ServiceApiTests(SimulatorWebApplicationFactory factory) => this.factory = factory;

    [Fact]
    public async Task SimulatorRequiresLeaseAndCompletesBoundedMotion()
    {
        using var client = factory.CreateClient();
        var startup = await client.GetFromJsonAsync<UvexDeviceStatus>("/api/v1/device");
        Assert.NotNull(startup);
        Assert.Equal(DeviceConnectionState.Disconnected, startup.ConnectionState);
        Assert.False(startup.PositionKnown);

        // Merely running the Windows/API service must not open the selected
        // device. This is deliberately analogous to opening PHD2 before the
        // operator presses Connect.
        await Task.Delay(250);
        Assert.Equal(
            DeviceConnectionState.Disconnected,
            (await client.GetFromJsonAsync<UvexDeviceStatus>("/api/v1/device"))?.ConnectionState);

        using var unauthorized = await client.PostAsJsonAsync("/api/v1/focus/move", new { deltaSteps = 1 });
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        using var unauthorizedIllumination = await client.PostAsJsonAsync("/api/v1/slit/illumination", new { enabled = true });
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorizedIllumination.StatusCode);

        using var leaseResponse = await client.PostAsJsonAsync("/api/v1/leases", new { owner = "integration-test", ttlSeconds = 30 });
        leaseResponse.EnsureSuccessStatusCode();
        var lease = await leaseResponse.Content.ReadFromJsonAsync<LeaseDto>();
        Assert.NotNull(lease);

        using var connect = new HttpRequestMessage(HttpMethod.Post, "/api/v1/device/connect")
        {
            Content = JsonContent.Create(new { }),
        };
        connect.Headers.Add("X-Uvex-Lease", lease.Token);
        using var connectAccepted = await client.SendAsync(connect);
        connectAccepted.EnsureSuccessStatusCode();
        var connectOperation = await connectAccepted.Content.ReadFromJsonAsync<OperationDto>();
        Assert.NotNull(connectOperation);
        Assert.Equal("Succeeded", (await WaitOperationAsync(client, connectOperation.Id)).State);

        var initial = await WaitReadyAsync(client);
        Assert.True(initial.PositionKnown);
        Assert.Equal(UvexPositionTrust.Live, initial.PositionTrust);
        Assert.Equal(["300um", "15um", "25um", "35um"], initial.Slits.Select(slit => slit.Name));
        Assert.All(initial.Slits, slit => Assert.Equal(0, slit.OffsetSteps));
        Assert.Equal(283, initial.SlitPhotodiodeThreshold);
        Assert.Equal(UvexOutputState.Unknown, initial.SlitIlluminationLedState);

        using var move = new HttpRequestMessage(HttpMethod.Post, "/api/v1/focus/move")
        {
            Content = JsonContent.Create(new { deltaSteps = 25 }),
        };
        move.Headers.Add("X-Uvex-Lease", lease.Token);
        using var accepted = await client.SendAsync(move);
        accepted.EnsureSuccessStatusCode();
        var operation = await accepted.Content.ReadFromJsonAsync<OperationDto>();
        Assert.NotNull(operation);

        var completed = await WaitOperationAsync(client, operation.Id);
        Assert.Equal("Succeeded", completed.State);
        var final = await client.GetFromJsonAsync<UvexDeviceStatus>("/api/v1/device");
        Assert.Equal(initial.FocusPositionSteps + 25, final?.FocusPositionSteps);

        using var reverseMove = new HttpRequestMessage(HttpMethod.Post, "/api/v1/focus/move")
        {
            Content = JsonContent.Create(new { deltaSteps = -10 }),
        };
        reverseMove.Headers.Add("X-Uvex-Lease", lease.Token);
        using var reverseAccepted = await client.SendAsync(reverseMove);
        reverseAccepted.EnsureSuccessStatusCode();
        var reverseOperation = await reverseAccepted.Content.ReadFromJsonAsync<OperationDto>();
        Assert.NotNull(reverseOperation);
        Assert.Equal("Succeeded", (await WaitOperationAsync(client, reverseOperation.Id)).State);

        var reversed = await client.GetFromJsonAsync<UvexDeviceStatus>("/api/v1/device");
        Assert.Equal(initial.FocusPositionSteps + 15, reversed?.FocusPositionSteps);

        using var unconfirmed = await client.PostAsJsonAsync("/api/v1/slit/calibrate-position", new { position = 3, confirmed = false });
        Assert.Equal(HttpStatusCode.BadRequest, unconfirmed.StatusCode);

        using var calibrate = new HttpRequestMessage(HttpMethod.Post, "/api/v1/slit/calibrate-position")
        {
            Content = JsonContent.Create(new { position = 3, confirmed = true }),
        };
        calibrate.Headers.Add("X-Uvex-Lease", lease.Token);
        using var calibrationAccepted = await client.SendAsync(calibrate);
        calibrationAccepted.EnsureSuccessStatusCode();
        var calibrationOperation = await calibrationAccepted.Content.ReadFromJsonAsync<OperationDto>();
        Assert.NotNull(calibrationOperation);
        Assert.Equal("Succeeded", (await WaitOperationAsync(client, calibrationOperation.Id)).State);

        var calibrated = await client.GetFromJsonAsync<UvexDeviceStatus>("/api/v1/device");
        Assert.Equal(3, calibrated?.SlitPosition);

        using var unconfirmedOpenLoop = new HttpRequestMessage(HttpMethod.Post, "/api/v1/slit/select-open-loop")
        {
            Content = JsonContent.Create(new { position = 4, confirmed = false }),
        };
        unconfirmedOpenLoop.Headers.Add("X-Uvex-Lease", lease.Token);
        using var unconfirmedOpenLoopResponse = await client.SendAsync(unconfirmedOpenLoop);
        Assert.Equal(HttpStatusCode.BadRequest, unconfirmedOpenLoopResponse.StatusCode);

        using var openLoop = new HttpRequestMessage(HttpMethod.Post, "/api/v1/slit/select-open-loop")
        {
            Content = JsonContent.Create(new { position = 4, confirmed = true }),
        };
        openLoop.Headers.Add("X-Uvex-Lease", lease.Token);
        using var openLoopAccepted = await client.SendAsync(openLoop);
        openLoopAccepted.EnsureSuccessStatusCode();
        var openLoopOperation = await openLoopAccepted.Content.ReadFromJsonAsync<OperationDto>();
        Assert.NotNull(openLoopOperation);
        Assert.Equal("Succeeded", (await WaitOperationAsync(client, openLoopOperation.Id)).State);
        Assert.Equal(4, (await client.GetFromJsonAsync<UvexDeviceStatus>("/api/v1/device"))?.SlitPosition);

        using var offsetCalibration = new HttpRequestMessage(HttpMethod.Post, "/api/v1/slit/calibrate-offset")
        {
            Content = JsonContent.Create(new { position = 3, offsetSteps = -12, confirmed = true }),
        };
        offsetCalibration.Headers.Add("X-Uvex-Lease", lease.Token);
        using var offsetAccepted = await client.SendAsync(offsetCalibration);
        offsetAccepted.EnsureSuccessStatusCode();
        var offsetOperation = await offsetAccepted.Content.ReadFromJsonAsync<OperationDto>();
        Assert.NotNull(offsetOperation);
        Assert.Equal("Succeeded", (await WaitOperationAsync(client, offsetOperation.Id)).State);

        var offsetCalibrated = await client.GetFromJsonAsync<UvexDeviceStatus>("/api/v1/device");
        Assert.Equal(-12, offsetCalibrated?.Slits.Single(slit => slit.Position == 3).OffsetSteps);

        using var illuminationOn = new HttpRequestMessage(HttpMethod.Post, "/api/v1/slit/illumination")
        {
            Content = JsonContent.Create(new { enabled = true }),
        };
        illuminationOn.Headers.Add("X-Uvex-Lease", lease.Token);
        using var illuminationAccepted = await client.SendAsync(illuminationOn);
        illuminationAccepted.EnsureSuccessStatusCode();
        var illuminationOperation = await illuminationAccepted.Content.ReadFromJsonAsync<OperationDto>();
        Assert.NotNull(illuminationOperation);
        Assert.Equal("Succeeded", (await WaitOperationAsync(client, illuminationOperation.Id)).State);

        var illuminated = await client.GetFromJsonAsync<UvexDeviceStatus>("/api/v1/device");
        Assert.Equal(UvexOutputState.On, illuminated?.SlitIlluminationLedState);
        Assert.NotNull(illuminated?.SlitIlluminationLedCommandedUtc);
        Assert.True(illuminated?.SlitPhotodiodeValue > illuminated?.SlitPhotodiodeThreshold);

        using var stopAccepted = await client.PostAsJsonAsync("/api/v1/device/stop", new { });
        stopAccepted.EnsureSuccessStatusCode();
        var stopOperation = await stopAccepted.Content.ReadFromJsonAsync<OperationDto>();
        Assert.NotNull(stopOperation);
        Assert.Equal("Succeeded", (await WaitOperationAsync(client, stopOperation.Id)).State);

        var stopped = await client.GetFromJsonAsync<UvexDeviceStatus>("/api/v1/device");
        Assert.Equal(UvexOutputState.Off, stopped?.SlitIlluminationLedState);

        using var illuminationOnAgain = new HttpRequestMessage(HttpMethod.Post, "/api/v1/slit/illumination")
        {
            Content = JsonContent.Create(new { enabled = true }),
        };
        illuminationOnAgain.Headers.Add("X-Uvex-Lease", lease.Token);
        using var illuminationAgainAccepted = await client.SendAsync(illuminationOnAgain);
        illuminationAgainAccepted.EnsureSuccessStatusCode();
        var illuminationAgainOperation = await illuminationAgainAccepted.Content.ReadFromJsonAsync<OperationDto>();
        Assert.NotNull(illuminationAgainOperation);
        Assert.Equal("Succeeded", (await WaitOperationAsync(client, illuminationAgainOperation.Id)).State);

        using var illuminationOff = new HttpRequestMessage(HttpMethod.Post, "/api/v1/slit/illumination")
        {
            Content = JsonContent.Create(new { enabled = false }),
        };
        illuminationOff.Headers.Add("X-Uvex-Lease", lease.Token);
        using var illuminationOffAccepted = await client.SendAsync(illuminationOff);
        illuminationOffAccepted.EnsureSuccessStatusCode();
        var illuminationOffOperation = await illuminationOffAccepted.Content.ReadFromJsonAsync<OperationDto>();
        Assert.NotNull(illuminationOffOperation);
        Assert.Equal("Succeeded", (await WaitOperationAsync(client, illuminationOffOperation.Id)).State);

        var dark = await client.GetFromJsonAsync<UvexDeviceStatus>("/api/v1/device");
        Assert.Equal(UvexOutputState.Off, dark?.SlitIlluminationLedState);
        Assert.True(dark?.SlitPhotodiodeValue < dark?.SlitPhotodiodeThreshold);

        using var disconnect = new HttpRequestMessage(HttpMethod.Post, "/api/v1/device/disconnect")
        {
            Content = JsonContent.Create(new { }),
        };
        disconnect.Headers.Add("X-Uvex-Lease", lease.Token);
        using var disconnectAccepted = await client.SendAsync(disconnect);
        disconnectAccepted.EnsureSuccessStatusCode();
        var disconnectOperation = await disconnectAccepted.Content.ReadFromJsonAsync<OperationDto>();
        Assert.NotNull(disconnectOperation);
        Assert.Equal("Succeeded", (await WaitOperationAsync(client, disconnectOperation.Id)).State);

        var disconnected = await client.GetFromJsonAsync<UvexDeviceStatus>("/api/v1/device");
        Assert.Equal(DeviceConnectionState.Disconnected, disconnected?.ConnectionState);
        Assert.False(disconnected?.PositionKnown);

        // The former implementation reconnected every five seconds. Waiting
        // through that interval protects the operator-visible Disconnect
        // contract against regression.
        await Task.Delay(TimeSpan.FromSeconds(5.5));
        var stillDisconnected = await client.GetFromJsonAsync<UvexDeviceStatus>("/api/v1/device");
        Assert.Equal(DeviceConnectionState.Disconnected, stillDisconnected?.ConnectionState);
        Assert.False(stillDisconnected?.PositionKnown);

        using var release = await client.DeleteAsync($"/api/v1/leases/{lease.Token}");
        release.EnsureSuccessStatusCode();
    }

    private static async Task<UvexDeviceStatus> WaitReadyAsync(HttpClient client)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var status = await client.GetFromJsonAsync<UvexDeviceStatus>("/api/v1/device");
            if (status?.ConnectionState == DeviceConnectionState.Ready)
            {
                return status;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("Simulator did not become ready.");
    }

    private static async Task<OperationDto> WaitOperationAsync(HttpClient client, Guid id)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var operation = await client.GetFromJsonAsync<OperationDto>($"/api/v1/operations/{id}");
            if (operation?.State is "Succeeded" or "Failed" or "Cancelled")
            {
                return operation;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("Operation did not complete.");
    }

    private sealed record LeaseDto(string Token, string Owner, DateTimeOffset ExpiresUtc);
    private sealed record OperationDto(Guid Id, string Kind, string State, string? Error);
}

public sealed class SimulatorWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string dataDirectory = Path.Combine(Path.GetTempPath(), "UVEX-ADV.Tests", Guid.NewGuid().ToString("N"));
    private readonly string? previousDataDirectory;

    public SimulatorWebApplicationFactory()
    {
        previousDataDirectory = Environment.GetEnvironmentVariable("UVEX_ADV_DATA_DIR");
        Environment.SetEnvironmentVariable("UVEX_ADV_DATA_DIR", dataDirectory);
    }

    protected override void Dispose(bool disposing)
    {
        try
        {
            base.Dispose(disposing);
        }
        finally
        {
            Environment.SetEnvironmentVariable("UVEX_ADV_DATA_DIR", previousDataDirectory);
            if (Directory.Exists(dataDirectory))
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
        }
    }
}
