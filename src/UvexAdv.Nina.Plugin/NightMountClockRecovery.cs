using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

// This is a lifecycle recovery, not a target-quality whitelist. A night may
// continue only after an affirmative sole-owner time repair at a joined boundary.
internal sealed class NightMountClockRecovery
{
    internal bool Attempted { get; private set; }
    internal static bool IsClockFailure(GateResult? gate) => gate is { Disposition: not GateDisposition.Passed } &&
        gate.Code is "MOUNT_CLOCK_RECHECK_REQUIRED" or "MOUNT_CLOCK_OFFSET_EXCEEDED" or "MOUNT_CLOCK_UNAVAILABLE";

    internal async Task<GateResult?> TryRecoverAsync(GateResult? failure, bool boundaryEligible,
        Func<CancellationToken, Task> persistIntent,
        Func<CancellationToken, Task<GateResult>> recover, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!boundaryEligible || Attempted || !IsClockFailure(failure)) return null;
        Attempted = true; // Precharge before persistence and any equipment action.
        try
        {
            await persistIntent(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return await recover(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return GateResult.Unknown("NIGHT_MOUNT_CLOCK_RECOVERY_FAILED", ex.Message);
        }
    }
}
