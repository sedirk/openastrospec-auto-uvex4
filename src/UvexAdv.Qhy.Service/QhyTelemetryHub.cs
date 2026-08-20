using Microsoft.AspNetCore.SignalR;
using UvexAdv.Qhy.Core;

namespace UvexAdv.Qhy.Service;

public sealed class QhyTelemetryHub : Hub;

public sealed class QhyTelemetryPublisher(
    QhyJobCoordinator coordinator,
    IHubContext<QhyTelemetryHub> hub,
    ILogger<QhyTelemetryPublisher> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        coordinator.JobChanged += Publish;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        coordinator.JobChanged -= Publish;
        return Task.CompletedTask;
    }

    private void Publish(QhyJobSnapshot snapshot)
    {
        _ = PublishAsync(snapshot);
    }

    private async Task PublishAsync(QhyJobSnapshot snapshot)
    {
        try
        {
            await hub.Clients.All.SendAsync("jobChanged", snapshot).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not publish QHY job {JobId} telemetry.", snapshot.Id);
        }
    }
}

public sealed class QhyAutoConnectHostedService(
    QhyJobCoordinator coordinator,
    QhyServiceOptions options,
    ILogger<QhyAutoConnectHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.AutoConnect) return;
        try
        {
            await coordinator.ConnectCameraAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "QHY auto-connect failed. The service remains available for operator diagnosis and retry.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
