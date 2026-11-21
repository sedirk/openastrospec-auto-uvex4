using System.Diagnostics;
using NINA.Core.Model;
using NINA.Equipment.Interfaces.Mediator;
using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

internal sealed partial class RealObservationStageRunner
{
    // Explicit native-night options only; the opening path supplies a live
    // safety guard and tracks the original native task. Never a reconnect repair.
    internal static async Task<GateResult> HomeAndVerifyMountAsync(ITelescopeMediator mediator,
        string expectedId, IProgress<ApplicationStatus> progress, TimeSpan timeout,
        TimeSpan pollInterval, CancellationToken token,
        Func<GateResult?>? openingGuard = null, Action<Task>? retainOwnerTask = null)
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
        string? ReadIssue(bool beforeCommand, bool allowResidualTracking = false)
        {
            var info = mediator.GetInfo();
            if (!info.Connected || !EnvironmentAdapterIdentityMatches(info.DeviceId, expectedId))
                return "Mount connection/identity changed.";
            if (info.AtPark) return "Mount is parked; Home does not unpark it.";
            if (info.IsPulseGuiding || beforeCommand && (info.Slewing || !allowResidualTracking && info.TrackingEnabled))
                return "Mount motion/guiding is not idle.";
            if (!info.CanFindHome) return "Mount does not support Home.";
            return null;
        }
        void StopAfterFailure()
        {
            if (commands == 0 || stopRequested) return;
            var info = mediator.GetInfo();
            // Never send commands to a newly selected or disconnected adapter.
            if (!info.Connected || !EnvironmentAdapterIdentityMatches(info.DeviceId, expectedId)) return;
            stopRequested = true;
            mediator.StopSlew();
            info = mediator.GetInfo();
            if (info.Connected && EnvironmentAdapterIdentityMatches(info.DeviceId, expectedId) && info.TrackingEnabled)
                _ = mediator.SetTrackingEnabled(false);
        }
        void CheckOpening()
        {
            bounded.Token.ThrowIfCancellationRequested();
            if (openingGuard?.Invoke() is { } gate) throw new PhysicalActionGateException(gate);
            bounded.Token.ThrowIfCancellationRequested();
        }
        async Task<T> AwaitOwner<T>(Task<T> task)
        {
            retainOwnerTask?.Invoke(task); // Retain real task even if the driver ignores cancellation.
            if (openingGuard is not null)
            {
                while (!task.IsCompleted)
                {
                    CheckOpening();
                    await Task.WhenAny(task, Task.Delay(pollInterval, bounded.Token)).ConfigureAwait(false);
                }
            }
            return await task.WaitAsync(bounded.Token).ConfigureAwait(false);
        }
        try
        {
            if (openingGuard is not null)
            {
                CheckOpening();
                if (ReadIssue(true, allowResidualTracking: true) is { } issue)
                    return Result("NIGHT_HOME_PRECONDITION", issue);
                // Only residual sidereal tracking may be stopped. Never abort
                // an operator's pre-existing slew/guide session to make it eligible.
                if (mediator.GetInfo().TrackingEnabled)
                {
                    if (!mediator.GetInfo().CanSetTrackingEnabled)
                        return Result("NIGHT_HOME_PRECONDITION", "Mount cannot disable residual tracking.");
                    _ = await AwaitOwner(Task.Run(() =>
                    {
                        CheckOpening();
                        if (ReadIssue(true, allowResidualTracking: true) is { } changed) throw new InvalidOperationException(changed);
                        return mediator.SetTrackingEnabled(false); // false is the requested state, not rejection
                    }, bounded.Token)).ConfigureAwait(false);
                }
                var stopped = 0;
                while (stopped < 2)
                {
                    CheckOpening();
                    if (ReadIssue(true, allowResidualTracking: true) is { } changed)
                        return Result("NIGHT_HOME_PRECONDITION", changed);
                    stopped = mediator.GetInfo().TrackingEnabled ? 0 : stopped + 1;
                    if (stopped < 2) await Task.Delay(pollInterval, bounded.Token).ConfigureAwait(false);
                }
            }
            CheckOpening();
            if (ReadIssue(beforeCommand: true) is { } before)
                return Result("NIGHT_HOME_PRECONDITION", before);
            if (!mediator.GetInfo().AtHome)
            {
                commands++;
                // Await native completion as well as fresh state. A positive
                // acknowledgement alone is not arrival, nor is a timed-out task.
                if (!await AwaitOwner(mediator.FindHome(progress, bounded.Token)).ConfigureAwait(false))
                {
                    StopAfterFailure();
                    return Result("NIGHT_HOME_REJECTED", "N.I.N.A. did not complete Home; Park not authorized.");
                }
            }
            var stable = 0;
            var trackingStopSent = false;
            while (stable < 2)
            {
                CheckOpening();
                if (ReadIssue(beforeCommand: false) is { } issue)
                {
                    StopAfterFailure();
                    return Result("NIGHT_HOME_STATE_CHANGED", issue);
                }
                var info = mediator.GetInfo();
                if (info.AtHome && !info.Slewing && info.TrackingEnabled && !trackingStopSent)
                {
                    trackingStopSent = true;
                    if (openingGuard is null) _ = mediator.SetTrackingEnabled(false);
                    else _ = await AwaitOwner(Task.Run(() =>
                    {
                        CheckOpening();
                        if (ReadIssue(false) is not null || !mediator.GetInfo().AtHome || mediator.GetInfo().Slewing)
                            throw new InvalidOperationException("Home/owner state changed before stopping tracking.");
                        return mediator.SetTrackingEnabled(false);
                    }, bounded.Token)).ConfigureAwait(false);
                }
                stable = info.AtHome && !info.Slewing && !info.IsPulseGuiding && !info.TrackingEnabled
                    ? stable + 1 : 0;
                if (stable < 2) await Task.Delay(pollInterval, bounded.Token).ConfigureAwait(false);
            }
            verified = true;
            return Result("NIGHT_HOME_VERIFIED", "Home confirmed stopped in two owner readbacks; no Park or target slew issued here.", true);
        }
        catch (PhysicalActionGateException ex)
        {
            bounded.Cancel();
            StopAfterFailure();
            return ex.ToStageResult().Gate;
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
            try { if (openingGuard is not null) bounded.Cancel(); StopAfterFailure(); }
            catch (Exception stop) { return Result("NIGHT_HOME_STOP_FAILED", $"{ex.Message}; stop: {stop.Message}"); }
            return Result("NIGHT_HOME_FAILED", ex.Message);
        }
    }
}
