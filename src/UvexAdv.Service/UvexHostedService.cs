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
                    // The service owns the protocol session, but service lifetime is
                    // deliberately not device-connection lifetime.  Like PHD2, it
                    // starts disconnected and only opens COM5 after an explicit
                    // operator/API connect request.  A normal disconnect must remain
                    // disconnected; otherwise the old five-second retry loop races
                    // the vendor application and makes the Disconnect button a lie.
                    if (controller.UnexpectedTransportRecoveryPending)
                    {
                        if (await controller.TryRecoverUnexpectedTransportLossAsync(stoppingToken).ConfigureAwait(false))
                        {
                            logger.LogInformation(
                                "UVEX unexpected transport loss recovered on {PortName}; identity, slit configuration and live positions were re-read",
                                controller.Status.PortName);
                        }
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
                    if (!controller.UnexpectedTransportRecoveryPending &&
                        controller.Status.ConnectionState == DeviceConnectionState.Ready)
                    {
                        controller.MarkUnexpectedTransportLoss(ex);
                    }

                    logger.LogWarning(
                        ex,
                        controller.UnexpectedTransportRecoveryPending
                            ? "UVEX unexpected transport loss is pending; the next bounded background cycle will reopen only the configured port"
                            : "UVEX background status refresh failed without authorizing automatic reconnect");
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
