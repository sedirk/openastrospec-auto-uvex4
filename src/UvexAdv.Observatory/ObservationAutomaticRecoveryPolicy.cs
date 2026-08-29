namespace UvexAdv.Observatory;

/// <summary>
/// A deliberately small, whitelist-only set of recoveries that may run before
/// an observation is handed to the operator.  This policy never authorizes a
/// new physical action: it only asks the runner to repeat an idempotent state
/// read/capture, invalidate derived evidence, or execute an already-defined
/// resume-recovery chain.  Durable motion ledgers and their attempt/cumulative
/// budgets remain authoritative across every retry.
/// </summary>
public enum ObservationAutomaticRecoveryAction
{
    None,
    RetrySameStage,
    RetryWithFreshStageEvidence,
    RebuildStageDependencies,
    RetryTerminalCleanup,
}

public sealed record ObservationAutomaticRecoveryPlan(
    ObservationAutomaticRecoveryAction Action,
    int MaximumAttempts,
    TimeSpan Delay,
    string Reason)
{
    public bool IsRecoverable =>
        Action != ObservationAutomaticRecoveryAction.None && MaximumAttempts > 0;

    public static ObservationAutomaticRecoveryPlan HardStop(string reason) =>
        new(ObservationAutomaticRecoveryAction.None, 0, TimeSpan.Zero, reason);
}

public sealed record ObservationAutomaticRecoveryAttempt(
    ObservationAutomaticRecoveryPlan Plan,
    bool ShouldRetry,
    bool Exhausted,
    int AttemptNumber,
    int TotalAttempts);

/// <summary>
/// Run-local in-memory retry ledger. Each reviewed stage/code/action pair owns
/// its stated bound so a nested recovery does not consume an unrelated outer
/// failure's allowance. The total fence prevents alternating error codes from
/// manufacturing an unbounded loop; durable physical-motion ledgers remain
/// separate and authoritative.
/// </summary>
public sealed class ObservationAutomaticRecoverySession
{
    public const int MaximumTotalAttempts = 8;

    private readonly Dictionary<(ObservationStage Stage, string Code, ObservationAutomaticRecoveryAction Action), int> attempts = new();
    private int totalAttempts;

    public ObservationAutomaticRecoveryAttempt Evaluate(
        ObservationStage stage,
        GateResult gate)
    {
        var plan = ObservationAutomaticRecoveryPolicy.For(stage, gate);
        if (!plan.IsRecoverable)
        {
            return new ObservationAutomaticRecoveryAttempt(
                plan,
                ShouldRetry: false,
                Exhausted: false,
                AttemptNumber: 0,
                TotalAttempts: totalAttempts);
        }

        var key = (stage, gate.Code, plan.Action);
        attempts.TryGetValue(key, out var consumed);
        if (consumed >= plan.MaximumAttempts || totalAttempts >= MaximumTotalAttempts)
        {
            return new ObservationAutomaticRecoveryAttempt(
                plan,
                ShouldRetry: false,
                Exhausted: true,
                AttemptNumber: consumed,
                TotalAttempts: totalAttempts);
        }

        consumed++;
        totalAttempts++;
        attempts[key] = consumed;
        return new ObservationAutomaticRecoveryAttempt(
            plan,
            ShouldRetry: true,
            Exhausted: false,
            AttemptNumber: consumed,
            TotalAttempts: totalAttempts);
    }
}

public static class ObservationAutomaticRecoveryPolicy
{
    /// <summary>
    /// Returns an automatic recovery only for an exact, reviewed stage/code
    /// pair.  Unknown and newly introduced errors are hard stops by default.
    /// </summary>
    public static ObservationAutomaticRecoveryPlan For(
        ObservationStage stage,
        GateResult gate)
    {
        if (gate.Disposition == GateDisposition.Passed)
        {
            return ObservationAutomaticRecoveryPlan.HardStop("The stage already passed.");
        }

        return (stage, gate.Code) switch
        {
            // Connecting an already identity-pinned N.I.N.A./PHD2/UVEX
            // adapter is idempotent. Every retry reruns owner/profile checks;
            // identity drift and unsafe live state use different codes and do
            // not inherit this transport-only recovery.
            (ObservationStage.ValidateNightSetup,
                "ATR_CONNECT_FAILED" or
                "ATR_NOT_CONNECTED" or
                "TELESCOPE_CONNECT_FAILED" or
                "TELESCOPE_NOT_CONNECTED" or
                "C11_MAIN_FOCUSER_CONNECT_FAILED" or
                "GUIDER_CONNECT_FAILED" or
                "NINA_GUIDER_NOT_CONNECTED" or
                "SAFETY_MONITOR_CONNECT_FAILED" or
                "WEATHER_CONNECT_FAILED" or
                "ROOF_ADAPTER_CONNECT_FAILED" or
                "OPTICAL_COVER_CONNECT_FAILED" or
                "NINA_EQUIPMENT_CONNECT_EXCEPTION" or
                "UVEX_AUTO_CONNECT_FAILED" or
                "UVEX_NOT_READY") => RetrySameStage(
                    2,
                    "Repeat the identity-pinned connection/readback boundary; no movement, exposure or output command is authorized by the retry.",
                    TimeSpan.FromSeconds(1)),

            // Tracking enable is idempotent.  The stage still has to obtain a
            // positive readback before its catalogue slew is allowed.
            (ObservationStage.SlewToCatalogTarget,
                "TELESCOPE_TRACKING_ENABLE_FAILED" or
                "TELESCOPE_TRACKING_ENABLE_TIMEOUT" or
                "TELESCOPE_TRACKING_CONNECTION_LOST") => RetrySameStage(
                    2,
                    "Re-read/re-establish tracking before the catalogue slew; no slew is authorized by this retry."),

            // Coarse centering cannot use a missing/stale accepted source
            // frame. Re-enter the existing resume dependency chain, which
            // reacquires QHY once before trying coarse centering again.
            (ObservationStage.CoarseCenter,
                "QHY_SOLVE_REQUIRED" or
                "QHY_COARSE_SOURCE_FRAME_MISSING" or
                "QHY_NATIVE_CENTER_SOURCE_FRAME_MISSING" or
                "QHY_NATIVE_CENTER_FRESH_SOURCE_REQUIRED") => RebuildDependencies(
                    1,
                    "Reacquire one accepted immutable QHY field and solve before re-entering coarse centering."),

            // These failures describe disposable detector evidence at an
            // unchanged logical acquisition stage.  A retry must create a new
            // immutable FITS/mount binding and may not reset any motion ledger.
            (ObservationStage.AcquireG3SlitField,
                "G3_FIELD_MOUNT_BINDING_STALE" or
                "G3_FIELD_MOUNT_BINDING_FRAME_MISSING" or
                "G3_FIELD_MOUNT_BINDING_FRAME_UNREADABLE" or
                "G3_FIELD_MOUNT_BINDING_READBACK_UNAVAILABLE" or
                "G3_TRUSTED_PL3_CARRY_FORWARD_UNAVAILABLE" or
                "G3_TRUSTED_PL3_CARRY_FORWARD_STALE" or
                "G3_PLATE_SOLVE_LADDER_TRANSIENT_EXHAUSTED" or
                "BRIGHT_TARGET_ONLY_ANNULAR_GHOSTS" or
                "BRIGHT_TARGET_TOPOLOGY_UNPROVEN" or
                "SLIT_LED_IDENTITY_GEOMETRY_UNAVAILABLE") => RetryFreshEvidence(
                    1,
                    "Discard the derived G3 field and acquire one independent immutable evidence set at the current ledgered position; morphology alone does not authorize motion or target identity."),

            // No coherent source means cloud/transparency cannot be separated
            // from an empty field. The code explicitly withholds mount search
            // motion, so take a few fresh frames at the same durable position
            // instead of pausing immediately or moving blindly.
            (ObservationStage.AcquireG3SlitField,
                "G3_CLOUD_OR_TRANSPARENCY_INVALID") => RetryFreshEvidence(
                    3,
                    "Wait briefly and acquire a fresh immutable G3 evidence set at the unchanged ledgered position; no mount search motion is authorized.",
                    TimeSpan.FromSeconds(5)),

            // The requested stage cannot proceed with its derived evidence,
            // but the existing resume policy already defines the earliest
            // safe dependency chain to rebuild.
            (ObservationStage.PlaceTargetOnSlit,
                "G3_TARGET_REQUIRED" or
                "G3_FIELD_MOUNT_BINDING_STALE" or
                "G3_FIELD_MOUNT_BINDING_FRAME_MISSING" or
                "G3_FIELD_MOUNT_BINDING_FRAME_UNREADABLE" or
                "G3_FIELD_MOUNT_BINDING_READBACK_UNAVAILABLE" or
                "PHD2_SLIT_TARGET_REQUIRED") => RebuildDependencies(
                    1,
                    "Discard the stale disposable field binding, reacquire a fresh immutable G3 target/slit field, and re-enter placement without resetting any motion ledger."),

            // This code is emitted only after a fresh residual proved that the
            // runtime lock returned to its durable origin, the lineage was
            // written as settled, and checked-stop succeeded. It explicitly
            // requests a new G3 field, so rebuild the existing dependency chain
            // once while preserving every consumed attempt/pixel/time budget.
            (ObservationStage.PlaceTargetOnSlit, "PHD2_LOCK_FAILURE_RETURNED") =>
                RebuildDependencies(
                    1,
                    "The PHD2 runtime lock is freshly verified at its durable origin and guiding is stopped; rebuild G3 and placement once without issuing a new budget."),

            (ObservationStage.StartGuiding, "G3_FIELD_REQUIRED") => RebuildDependencies(
                    1,
                    "Reacquire G3 and replace the target before starting a new guide epoch."),

            (ObservationStage.StartGuiding,
                "PHD2_PLACEMENT_SETTLE_STALE" or
                "PHD2_GRADED_GUIDING_FIELD_REQUIRED") => RebuildDependencies(
                    1,
                    "Discard the stale placement/settle epoch, reacquire G3, replace the target and establish one fresh guide epoch."),

            (ObservationStage.StartQhyPhotometry or
                ObservationStage.SelectAtrExposure or
                ObservationStage.RunScienceBlock,
                "GUIDING_LOST" or
                "GUIDING_UNSTABLE" or
                "GUIDING_NOT_STABLE") => RebuildDependencies(
                    1,
                    "Use the existing bounded G3, slit-placement and guiding recovery chain before another science exposure."),

            (ObservationStage.RunScienceBlock, "ATR_TIER_NOT_SELECTED") => RetryFreshEvidence(
                    1,
                    "Invalidate the missing ATR tier and run the existing bounded reprobe before another science exposure."),

            // Finalization commands are stop/off/close operations and are
            // deliberately idempotent.  They may be retried, but failure after
            // the bound still remains an operator-visible terminal blocker.
            (ObservationStage.FinalizeObservation, "FINALIZE_INCOMPLETE") =>
                new ObservationAutomaticRecoveryPlan(
                    ObservationAutomaticRecoveryAction.RetryTerminalCleanup,
                    2,
                    TimeSpan.FromMilliseconds(500),
                    "Retry the checked terminal cleanup without reopening or resuming any data owner."),

            // Exhausted searches, returned/ambiguous physical actions,
            // identities, hashes, topology, safety and budgets are omitted on
            // purpose.  A missing whitelist entry is a hard stop.
            _ => ObservationAutomaticRecoveryPlan.HardStop(
                "No reviewed idempotent recovery exists for this exact stage and gate code."),
        };
    }

    private static ObservationAutomaticRecoveryPlan RetrySameStage(
        int attempts,
        string reason,
        TimeSpan? delay = null) =>
        new(
            ObservationAutomaticRecoveryAction.RetrySameStage,
            attempts,
            delay ?? TimeSpan.FromMilliseconds(350),
            reason);

    private static ObservationAutomaticRecoveryPlan RetryFreshEvidence(
        int attempts,
        string reason,
        TimeSpan? delay = null) =>
        new(
            ObservationAutomaticRecoveryAction.RetryWithFreshStageEvidence,
            attempts,
            delay ?? TimeSpan.FromMilliseconds(250),
            reason);

    private static ObservationAutomaticRecoveryPlan RebuildDependencies(int attempts, string reason) =>
        new(
            ObservationAutomaticRecoveryAction.RebuildStageDependencies,
            attempts,
            TimeSpan.FromMilliseconds(250),
            reason);
}
