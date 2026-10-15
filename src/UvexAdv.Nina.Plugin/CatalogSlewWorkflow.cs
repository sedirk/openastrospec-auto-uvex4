using System.Diagnostics;
using System.IO;
using System.Text.Json;
using NINA.Equipment.Interfaces.Mediator;
using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

// Owns the initial catalogue command, not fine centering or a return vector.
// ADR-0038 permits the SAME command once after a tracking-enable timeout.
internal static class CatalogSlewWorkflow
{
    internal static async Task<GateResult> ExecuteAsync(
        GateResult tracking, string intentPath, object intent,
        Func<CancellationToken, Task<GateResult>> requireBoundary,
        Func<CancellationToken, Task<bool>> slew,
        ITelescopeMediator mount, string expectedId, CancellationToken token,
        TimeSpan? commandTimeout = null, TimeSpan? verificationTimeout = null,
        TimeSpan? quietPeriod = null, TimeSpan? pollInterval = null)
    {
        if (tracking.Disposition != GateDisposition.Passed && tracking.Code != "TELESCOPE_TRACKING_ENABLE_TIMEOUT")
            return tracking;
        token.ThrowIfCancellationRequested();
        if (File.Exists(intentPath)) return AlreadyAttempted();
        var gate = await requireBoundary(token).ConfigureAwait(false);
        if (gate.Disposition != GateDisposition.Passed) return gate;
        if (PreCommandIssue() is { } initialIssue) return initialIssue;

        // CreateNew is both the process-race guard and the restart guard. Even a
        // partial write/cancellation retains the marker; it never grants retry.
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(intentPath)!);
            await using var stream = new FileStream(intentPath, FileMode.CreateNew,
                FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
            await JsonSerializer.SerializeAsync(stream, new
            {
                schemaVersion = 1, createdUtc = DateTimeOffset.UtcNow,
                trackingTimeoutRecovery = tracking.Code == "TELESCOPE_TRACKING_ENABLE_TIMEOUT",
                originalTracking = tracking, intent, maximumCommands = 1,
            }, cancellationToken: token).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        catch (IOException) when (File.Exists(intentPath)) { return AlreadyAttempted(); }

        // Disk/RPC latency cannot make an earlier safety/owner readback current.
        gate = await requireBoundary(token).ConfigureAwait(false);
        if (gate.Disposition != GateDisposition.Passed) return gate;
        if (PreCommandIssue() is { } lateIssue) return lateIssue;
        token.ThrowIfCancellationRequested();
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token);
        bounded.CancelAfter(commandTimeout ?? TimeSpan.FromMinutes(2));
        try
        {
            if (!await slew(bounded.Token).ConfigureAwait(false))
                return GateResult.Unknown("CATALOG_SLEW_REJECTED",
                    "N.I.N.A./driver rejected the sole catalogue GOTO; no retry, home or limit change was requested.");
            await mount.WaitForSlew(bounded.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && bounded.IsCancellationRequested)
        {
            return GateResult.Unknown("CATALOG_SLEW_TIMEOUT",
                "The sole catalogue GOTO exceeded its bounded interval; normal failure cleanup must confirm stop. No retry is permitted.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return GateResult.Unknown("CATALOG_SLEW_EXCEPTION",
                $"The sole catalogue GOTO raised {ex.GetType().Name}: {ex.Message}; no command is repeated.");
        }

        // Slew success is not tracking or optical success. Only fresh stable
        // readbacks permit the existing QHY/G3 acquisition pipeline to begin.
        var watch = Stopwatch.StartNew();
        TimeSpan? trackingSince = null;
        var timeout = verificationTimeout ?? TimeSpan.FromSeconds(15);
        var quiet = quietPeriod ?? TimeSpan.FromSeconds(3);
        using var verification = CancellationTokenSource.CreateLinkedTokenSource(token);
        verification.CancelAfter(timeout);
        try
        {
            while (watch.Elapsed < timeout)
            {
                verification.Token.ThrowIfCancellationRequested();
                gate = await requireBoundary(verification.Token).ConfigureAwait(false);
                if (gate.Disposition != GateDisposition.Passed) return gate;
                verification.Token.ThrowIfCancellationRequested();
                var info = mount.GetInfo();
                if (!info.Connected || info.DeviceId != expectedId || info.AtPark || info.Slewing || info.IsPulseGuiding)
                    return GateResult.Unknown("CATALOG_SLEW_POST_STATE_INVALID",
                        "Post-GOTO mount identity, connection or stopped state is unconfirmed; no fresh exposure is authorized.");
                if (!info.TrackingEnabled) trackingSince = null;
                else
                {
                    trackingSince ??= watch.Elapsed;
                    if (watch.Elapsed - trackingSince.Value >= quiet)
                        return GateResult.Pass("CATALOG_SLEW_TRACKING_VERIFIED",
                            "The sole catalogue GOTO completed and tracking remained enabled. Fresh independent images must verify position; no optical success is claimed.");
                }
                await Task.Delay(pollInterval ?? TimeSpan.FromMilliseconds(250), verification.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && verification.IsCancellationRequested) { }
        return GateResult.Unknown("CATALOG_SLEW_TRACKING_NOT_RESTORED",
            "Tracking did not remain enabled after the sole catalogue GOTO; no extra enable request, slew or exposure is authorized.");

        GateResult? PreCommandIssue()
        {
            var info = mount.GetInfo();
            if (!info.Connected || string.IsNullOrWhiteSpace(expectedId) || info.DeviceId != expectedId ||
                info.AtPark || info.Slewing || info.IsPulseGuiding ||
                (tracking.Code != "TELESCOPE_TRACKING_ENABLE_TIMEOUT" && !info.TrackingEnabled))
                return GateResult.Unknown("CATALOG_SLEW_PRE_STATE_INVALID",
                    "Initial catalogue command prerequisites changed; no slew is authorized.");
            return null;
        }
    }

    internal static GateResult AlreadyAttempted() => GateResult.Unknown("CATALOG_SLEW_ALREADY_ATTEMPTED",
        "This run already reserved its initial catalogue GOTO. Preserve the intent and confirm stop; stage retry/resume cannot send it again.");
}
