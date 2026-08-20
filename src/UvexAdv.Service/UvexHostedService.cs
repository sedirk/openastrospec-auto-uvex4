using Microsoft.AspNetCore.SignalR;
using UvexAdv.Core;
using UvexAdv.Service.Persistence;

namespace UvexAdv.Service;

internal sealed class UvexHostedService(
    UvexDeviceController controller,
    UvexDatabase database,
    IHubContext<UvexStatusHub> hub,
    ILogger<UvexHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (database.GetLastDeviceStatus() is { } lastKnown)
        {
            controller.RestoreLastKnown(lastKnown);
        }

        controller.StatusChanged += OnStatusChanged;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (controller.Status.ConnectionState is DeviceConnectionState.Disconnected or DeviceConnectionState.Faulted)
                    {
                        await controller.ConnectAsync(stoppingToken).ConfigureAwait(false);
                    }
                    else if (controller.Status.ConnectionState == DeviceConnectionState.Ready)
                    {
                        await controller.RefreshAsync(stoppingToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "UVEX background connection/refresh failed; retrying without moving hardware");
                }

                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
            }
        }
        finally
        {
            controller.StatusChanged -= OnStatusChanged;
        }
    }

    private void OnStatusChanged(object? sender, UvexDeviceStatus status)
    {
        if (status.PositionKnown && status.PositionTrust == UvexPositionTrust.Live)
        {
            try
            {
                database.UpsertDeviceStatus(status);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to persist the latest trusted UVEX position");
            }
        }

        _ = hub.Clients.All.SendAsync("status", status);
    }
}
