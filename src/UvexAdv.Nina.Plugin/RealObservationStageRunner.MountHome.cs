using System.Diagnostics;
using NINA.Core.Model;
using NINA.Equipment.Interfaces.Mediator;
using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

internal sealed partial class RealObservationStageRunner
{
    // Explicit native-night option only. This is not a startup/reconnect repair
    // and never changes Park, limits, calibration or an old motion ledger.
    internal static async Task<GateResult> HomeAndVerifyMountAsync(ITelescopeMediator mediator,
        string expectedId, IProgress<ApplicationStatus> progress, TimeSpan timeout,
        TimeSpan pollInterval, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (timeout <= TimeSpan.Zero || pollInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        var elapsed = Stopwatch.StartNew();
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token);
        bounded.CancelAfter(timeout);
        var commands = 0;
        var verified = false;
        var stopRequested = false;
        GateResult Result(string code, string message, bool passed = false) => new(
            code, passed ? GateDisposition.Passed : GateDisposition.Indeterminate, message,
            new Dictionary<string, double>
            {
                ["commandCount"] = commands, ["verified"] = verified ? 1 : 0,
                ["stopRequested"] = stopRequested ? 1 : 0,
                ["elapsedMilliseconds"] = elapsed.Elapsed.TotalMilliseconds,
                ["timeoutSeconds"] = timeout.TotalSeconds,
            }, passed ? GateSeverity.Info : GateSeverity.Error);
        string? ReadIssue(bool beforeCommand)
        {
            var info = mediator.GetInfo();
            if (!info.Connected || !EnvironmentAdapterIdentityMatches(info.DeviceId, expectedId))
                return "Mount connection/identity changed.";
            if (info.AtPark) return "Mount is parked; Home does not unpark it.";
            if (info.IsPulseGuiding || beforeCommand && (info.Slewing || info.TrackingEnabled))
                return "Mount motion/guiding is not idle.";
            if (!info.CanFindHome) return "Mount does not support Home.";
            return null;
        }
        void StopAfterFailure()
        {
            if (commands == 0) return;
            var info = mediator.GetInfo();
            // Never send commands to a newly selected or disconnected adapter.
            if (!info.Connected || !EnvironmentAdapterIdentityMatches(info.DeviceId, expectedId)) return;
            stopRequested = true;
            mediator.StopSlew();
            if (mediator.GetInfo().TrackingEnabled) _ = mediator.SetTrackingEnabled(false);
        }
        try
        {
            if (ReadIssue(beforeCommand: true) is { } before)
                return Result("NIGHT_HOME_PRECONDITION", before);
            if (!mediator.GetInfo().AtHome)
            {
                commands++;
                // Await native completion as well as fresh state. A positive
                // acknowledgement alone is not arrival, nor is a timed-out task.
                if (!await mediator.FindHome(progress, bounded.Token).WaitAsync(bounded.Token).ConfigureAwait(false))
                {
                    StopAfterFailure();
                    return Result("NIGHT_HOME_REJECTED", "N.I.N.A. did not complete Home; Park not authorized.");
                }
            }
            var stable = 0;
            var trackingStopSent = false;
            while (stable < 2)
            {
                bounded.Token.ThrowIfCancellationRequested();
                if (ReadIssue(beforeCommand: false) is { } issue)
                {
                    StopAfterFailure();
                    return Result("NIGHT_HOME_STATE_CHANGED", issue);
                }
                var info = mediator.GetInfo();
                if (info.AtHome && !info.Slewing && info.TrackingEnabled && !trackingStopSent)
                {
                    trackingStopSent = true;
                    _ = mediator.SetTrackingEnabled(false);
                }
                stable = info.AtHome && !info.Slewing && !info.IsPulseGuiding && !info.TrackingEnabled
                    ? stable + 1 : 0;
                await Task.Delay(pollInterval, bounded.Token).ConfigureAwait(false);
            }
            verified = true;
            return Result("NIGHT_HOME_VERIFIED", "Home confirmed stopped in two owner readbacks; Park may follow.", true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            StopAfterFailure();
            throw;
        }
        catch (OperationCanceledException)
        {
            StopAfterFailure();
            return Result("NIGHT_HOME_TIMEOUT", "Home exceeded its bounded timeout; stop requested, no automatic retry or Park.");
        }
        catch (Exception ex)
        {
            try { StopAfterFailure(); }
            catch (Exception stop) { return Result("NIGHT_HOME_STOP_FAILED", $"{ex.Message}; stop: {stop.Message}"); }
            return Result("NIGHT_HOME_FAILED", ex.Message);
        }
    }
}
