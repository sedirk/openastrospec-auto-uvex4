using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using UvexAdv.Qhy.Core;
using UvexAdv.Qhy.Service.Adapters;

namespace UvexAdv.Qhy.Tests;

public sealed class QhyApiTests : IClassFixture<QhySimulatorWebApplicationFactory>
{
    private readonly QhySimulatorWebApplicationFactory factory;

    public QhyApiTests(QhySimulatorWebApplicationFactory factory) => this.factory = factory;

    [Fact]
    public async Task LoopbackApiRunsUnattendedJobAndServesPreviewAndManifest()
    {
        using var client = factory.CreateClient();
        Assert.IsType<SimulatedQhyCameraAdapter>(factory.Services.GetRequiredService<IQhyCameraAdapter>());
        var health = await client.GetFromJsonAsync<QhyServiceHealth>("/api/v1/health");
        Assert.NotNull(health);
        Assert.True(health.LoopbackOnly);
        Assert.Equal("UVEX-ADV-QHY", health.Service);
        Assert.Equal("ok", health.Status);
        Assert.True(health.Configuration.Simulator);
        Assert.Equal("simulator", health.Configuration.Adapter);
        Assert.Equal("QHYminiCam8M", health.Configuration.ExpectedModel);
        Assert.Equal("SIM-QHYMINICAM8M-TEST", health.Configuration.ExpectedStableId);
        Assert.Equal(string.Empty, health.Configuration.NativeSdkSha256);
        Assert.Equal(1, health.Configuration.NativeReadoutMode);
        Assert.True(QhyServiceConfigurationProof.IsSha256(health.Configuration.NativeFilterPositionsSha256));
        Assert.Empty(health.Configuration.Validate());
        Assert.True(QhyServiceConfigurationProof.IsSha256(health.Configuration.ConfigurationSha256));

        using var response = await client.PostAsJsonAsync("/api/v1/jobs/acquisition", new AcquisitionJobRequest(
            "api-run",
            "field",
            [0.01],
            10,
            256,
            QualityThresholds: new QhyQualityThresholds(
                MinimumDetectedStars: 1,
                MaximumSaturatedFraction: 0.1,
                MinimumTransparency: 0,
                DetectionSigma: 3)));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var ownerToken = Assert.Single(response.Headers.GetValues(QhyControlProtocol.OwnerTokenHeaderName));
        Assert.True(ownerToken.Length >= 40);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var startBody = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(ownerToken, startBody, StringComparison.Ordinal);
        Assert.DoesNotContain("ownerToken", startBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("controlLeaseId", startBody, StringComparison.OrdinalIgnoreCase);
        var started = System.Text.Json.JsonSerializer.Deserialize<QhyJobSnapshot>(
            startBody,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.NotNull(started);
        Assert.Null(started.ControlLeaseId);

        var completed = await WaitForTerminalAsync(client, started.Id);
        Assert.Equal(QhyJobState.Completed, completed.State);
        Assert.Equal(health.Configuration.ExpectedStableId, completed.ExpectedCameraStableId);
        Assert.Single(completed.Frames);
        Assert.Equal(1, completed.TotalFrameCount);
        Assert.Equal(1, completed.TotalAcceptedFrameCount);
        Assert.Equal(completed.AcceptedFrameId, completed.LastEvaluatedFrameId);
        Assert.True(completed.LastFramePassedQualityGate);
        Assert.Equal("R", completed.Frames[0].Settings.FilterName);

        var wheel = await client.GetFromJsonAsync<QhyFilterWheelStatus>("/api/v1/camera/filter-wheel");
        Assert.NotNull(wheel);
        Assert.True(wheel.PositionKnown);
        Assert.Equal(5, wheel.Position);
        Assert.Equal("R", wheel.FilterName);

        using var selectResponse = await client.PostAsJsonAsync(
            "/api/v1/camera/filter-wheel/select",
            new QhyFilterSelectionRequest("G"));
        selectResponse.EnsureSuccessStatusCode();
        var green = await selectResponse.Content.ReadFromJsonAsync<QhyFilterWheelStatus>();
        Assert.NotNull(green);
        Assert.Equal(4, green.Position);
        Assert.Equal("G", green.FilterName);

        using var preview = await client.GetAsync($"/api/v1/jobs/{started.Id}/preview");
        preview.EnsureSuccessStatusCode();
        Assert.Equal("image/png", preview.Content.Headers.ContentType?.MediaType);
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, (await preview.Content.ReadAsByteArrayAsync())[..8]);

        using var manifest = await client.GetAsync($"/api/v1/jobs/{started.Id}/manifest");
        manifest.EnsureSuccessStatusCode();
        Assert.Equal("application/json", manifest.Content.Headers.ContentType?.MediaType);
        var manifestJson = await manifest.Content.ReadAsStringAsync();
        Assert.DoesNotContain(ownerToken, manifestJson, StringComparison.Ordinal);
        Assert.DoesNotContain("ownerToken", manifestJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("controlLeaseId", manifestJson, StringComparison.OrdinalIgnoreCase);
        var persisted = System.Text.Json.JsonSerializer.Deserialize<QhyJobSnapshot>(
            manifestJson,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.NotNull(persisted);
        Assert.Equal(1, persisted.TotalAcceptedFrameCount);

        var publicJobJson = await client.GetStringAsync($"/api/v1/jobs/{started.Id}");
        Assert.DoesNotContain(ownerToken, publicJobJson, StringComparison.Ordinal);
        Assert.DoesNotContain("ownerToken", publicJobJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("controlLeaseId", publicJobJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnknownJobReturnsNotFound()
    {
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync(
            $"/api/v1/jobs/{Guid.NewGuid()}/pause",
            new QhyOwnerControlRequest("not-a-real-token"));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ResumeAndCancelApiRequireOwnerTokenAndAuditDeniedControl()
    {
        using var client = factory.CreateClient();
        using var startResponse = await client.PostAsJsonAsync("/api/v1/jobs/acquisition", new AcquisitionJobRequest(
            "api-owner-control",
            "field",
            [0.01],
            10,
            256,
            MaximumAttempts: 1,
            QualityThresholds: new QhyQualityThresholds(
                MinimumDetectedStars: 100_000,
                MaximumSaturatedFraction: 0.1,
                MinimumTransparency: 0,
                DetectionSigma: 3)));
        startResponse.EnsureSuccessStatusCode();
        var ownerToken = Assert.Single(startResponse.Headers.GetValues(QhyControlProtocol.OwnerTokenHeaderName));
        var started = await startResponse.Content.ReadFromJsonAsync<QhyJobSnapshot>();
        Assert.NotNull(started);
        await WaitForStateAsync(client, started.Id, QhyJobState.PausedNeedsAttention);

        using var anonymousResume = await client.PostAsJsonAsync(
            $"/api/v1/jobs/{started.Id}/resume",
            new QhyResumeRequest(string.Empty, Actor: "anonymous-test"));
        Assert.Equal(HttpStatusCode.Forbidden, anonymousResume.StatusCode);
        var stillPaused = await WaitForStateAsync(client, started.Id, QhyJobState.PausedNeedsAttention);
        Assert.Contains(stillPaused.Events, item => item.Kind == "control.denied");

        using var anonymousCancel = await client.PostAsJsonAsync(
            $"/api/v1/jobs/{started.Id}/cancel",
            new QhyOwnerControlRequest("wrong-token", "anonymous-test"));
        Assert.Equal(HttpStatusCode.Forbidden, anonymousCancel.StatusCode);
        using var ownerCancel = await client.PostAsJsonAsync(
            $"/api/v1/jobs/{started.Id}/cancel",
            new QhyOwnerControlRequest(ownerToken, "api-test-owner"));
        ownerCancel.EnsureSuccessStatusCode();
        await WaitForStateAsync(client, started.Id, QhyJobState.Cancelled);
    }

    private static async Task<QhyJobSnapshot> WaitForTerminalAsync(HttpClient client, Guid id)
    {
        QhyJobSnapshot? latest = null;
        for (var attempt = 0; attempt < 200; attempt++)
        {
            latest = await client.GetFromJsonAsync<QhyJobSnapshot>($"/api/v1/jobs/{id}");
            if (latest?.State is QhyJobState.Completed or QhyJobState.Faulted or QhyJobState.Cancelled) return latest;
            await Task.Delay(25);
        }

        throw new TimeoutException($"API job did not finish; latest={latest?.State}, error={latest?.Error}.");
    }

    private static async Task<QhyJobSnapshot> WaitForStateAsync(HttpClient client, Guid id, QhyJobState expected)
    {
        QhyJobSnapshot? latest = null;
        for (var attempt = 0; attempt < 200; attempt++)
        {
            latest = await client.GetFromJsonAsync<QhyJobSnapshot>($"/api/v1/jobs/{id}");
            if (latest?.State == expected) return latest;
            if (latest?.State is QhyJobState.Faulted or QhyJobState.Completed or QhyJobState.TakenOver)
            {
                throw new Xunit.Sdk.XunitException(
                    $"API job reached {latest.State} while waiting for {expected}: {latest.Error}");
            }
            await Task.Delay(25);
        }
        throw new TimeoutException($"API job did not reach {expected}; latest={latest?.State}, error={latest?.Error}.");
    }

}

public sealed class QhySimulatorWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string dataDirectory = Path.Combine(Path.GetTempPath(), "UVEX-ADV-QHY.ApiTests", Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("QhyApiTests");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            // TestServer must not inherit a machine production file, command-line
            // switch, or Qhy__* environment variable that could select hardware.
            configuration.Sources.Clear();
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Qhy:Simulator"] = "true",
                ["Qhy:SimulatorMode"] = "Synthetic",
                ["Qhy:ExpectedStableId"] = "SIM-QHYMINICAM8M-TEST",
                ["Qhy:ExpectedModel"] = "QHYminiCam8M",
                ["Qhy:DataRoot"] = dataDirectory,
                ["Qhy:AutoConnect"] = "false",
                ["Qhy:SyntheticWidth"] = "160",
                ["Qhy:SyntheticHeight"] = "120",
                ["Qhy:SyntheticStars"] = "20",
                ["Qhy:SimulationDelayMilliseconds"] = "5",
                ["Qhy:NativeSdkPath"] = string.Empty,
                ["Qhy:NativeSdkSha256"] = string.Empty,
                ["Qhy:NativeReadoutMode"] = "1",
                ["Qhy:NativeFilterPositions:G"] = "4",
                ["Qhy:NativeFilterPositions:R"] = "5",
            });
        });
    }

    protected override void Dispose(bool disposing)
    {
        try
        {
            base.Dispose(disposing);
        }
        finally
        {
            if (Directory.Exists(dataDirectory)) Directory.Delete(dataDirectory, recursive: true);
        }
    }
}
