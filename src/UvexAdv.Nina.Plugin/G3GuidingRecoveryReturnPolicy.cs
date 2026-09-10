using UvexAdv.Observatory;
using UvexAdv.Phd2;

namespace UvexAdv.Nina.Plugin;

// An in-process, single-recovery proof; it is never restored after reconnect or
// restart. The immutable receipt records the identity/lock checks before stop.
internal sealed record Phd2DependencyRebuildStopProof(long ConnectionEpoch, long GuideEpoch, string EvidencePath)
{
    internal bool IsCurrent(Phd2StateSnapshot state) =>
        !string.IsNullOrWhiteSpace(EvidencePath) &&
        state.IsConnected && !state.AutomationPaused && !state.Phd2Paused &&
        state.AppState == Phd2AppState.Stopped && state.PendingSettleOperationId is null &&
        state.ConnectionEpoch == ConnectionEpoch && state.GuideEpoch == GuideEpoch;
}

internal sealed record G3GuidingRecoveryReturnPlan(
    GateResult Gate, G3AcquisitionMotionState? ChargedLedger = null,
    double ContinuityDeltaArcseconds = 0, int ReservedReturnActions = 0);

internal static class G3GuidingRecoveryReturnPolicy
{
    internal static G3GuidingRecoveryReturnPlan Plan(
        G3AcquisitionMotionState state, string runId, double reportedRa, double reportedDec,
        string epoch, string pierSide, bool ownedGuideStopConfirmed,
        double handoffToleranceArcseconds, double nearOriginToleranceArcseconds, DateTimeOffset now)
    {
        G3GuidingRecoveryReturnPlan Block(string code, string message) => new(GateResult.Unknown(code, message));
        if (state.Validate().Count != 0 || state.Phase != G3AcquisitionMotionPhase.SettledBudgetLedger ||
            state.ObservationRunId != runId || state.CoordinateEpoch != epoch || state.PierSide != pierSide ||
            now < state.UpdatedUtc)
            return Block("G3_GUIDING_RECOVERY_CONTEXT_INVALID", "导星后回程的原账本、运行、历元或赤道仪侧不一致；不接续运动。");
        var offset = G3AcquisitionMotionPlanner.SignedTangentOffsetArcseconds(
            state.OriginRaDegrees, state.OriginDeclinationDegrees, reportedRa, reportedDec);
        var delta = Math.Max(G3AcquisitionMotionPlanner.AngularSeparationArcseconds(
                state.PriorReportedRaDegrees, state.PriorReportedDeclinationDegrees, reportedRa, reportedDec),
            Math.Sqrt(Math.Pow(offset.RaArcseconds - state.CurrentRaTangentOffsetArcseconds, 2) +
                Math.Pow(offset.DecArcseconds - state.CurrentDeclinationOffsetArcseconds, 2)));
        var radius = G3AcquisitionMotionPlanner.AngularSeparationArcseconds(
            state.OriginRaDegrees, state.OriginDeclinationDegrees, reportedRa, reportedDec);
        var continuityLimit = Math.Max(handoffToleranceArcseconds, state.ArrivalToleranceArcseconds) +
            2 * state.ArrivalToleranceArcseconds;
        if (!double.IsFinite(delta) || !double.IsFinite(radius) ||
            !double.IsFinite(handoffToleranceArcseconds) || handoffToleranceArcseconds <= 0 ||
            !double.IsFinite(nearOriginToleranceArcseconds) || nearOriginToleranceArcseconds < state.ArrivalToleranceArcseconds ||
            2 * nearOriginToleranceArcseconds >= state.MaximumSingleCorrectionArcseconds)
            return Block("G3_GUIDING_RECOVERY_GEOMETRY_INVALID", "导星后回程坐标或容差无效；不发送运动。");
        if (delta <= continuityLimit)
            return new(GateResult.Pass("G3_GUIDING_RECOVERY_CONTINUITY_UNCHANGED", "位置仍在原接续范围内；无需额外回程。"));
        if (!ownedGuideStopConfirmed)
            return Block("G3_GUIDING_RECOVERY_STOP_REQUIRED", "位置已离开粗定位接续范围，但未确认本轮导星停止；不接管外部会话。");
        if (delta > state.MaximumSingleCorrectionArcseconds || radius > state.MaximumRadiusArcseconds)
            return Block("G3_GUIDING_RECOVERY_POSITION_LIMIT", "导星后位置差或原点半径超过原有运动范围；需要核验回零，未扩大门限。");

        // Charge the unaccounted endpoint displacement, not a fabricated new
        // slew. Keep the OLD position: this is return-only accounting, never
        // authority to adopt the new point for an outbound/search command.
        var charged = state with
        {
            CumulativeMotionArcseconds = state.CumulativeMotionArcseconds + delta,
            CorrectionAttempts = state.CorrectionAttempts + 1,
            UpdatedUtc = now,
            LastReason = $"Owned guiding ended; {delta:F2} arcsec and one accounting action reserved for return-only recovery. No N.I.N.A. command sent; origin/limits/clock unchanged.",
        };
        var returnActionCount = radius <= nearOriginToleranceArcseconds ? 0 :
            Math.Ceiling(radius / (state.MaximumSingleCorrectionArcseconds - 2 * nearOriginToleranceArcseconds));
        if (!double.IsFinite(returnActionCount) || returnActionCount > int.MaxValue)
            return Block("G3_GUIDING_RECOVERY_RETURN_RESERVE_LIMIT", "完整回程的分段数无效；未发送运动。");
        var returnActions = (int)returnActionCount;
        // Match the production return's full single-segment precharge, and
        // reserve every worst-case return segment before starting any of them.
        if (charged.CorrectionAttempts + (long)returnActions > state.MaximumCorrectionAttempts ||
            charged.CumulativeMotionArcseconds + returnActions * state.MaximumSingleCorrectionArcseconds > state.MaximumCumulativeMotionArcseconds ||
            (now - state.StartedUtc).TotalSeconds + (1d + returnActions) * state.WorstCaseActionSeconds > state.MaximumElapsedSeconds)
            return Block("G3_GUIDING_RECOVERY_RETURN_RESERVE_LIMIT", "原动作、位移或时间预算不足以覆盖导星位置差及完整回程；未重置预算，请核验回零后新开一轮。");
        var next = G3AcquisitionMotionPlanner.PlanNextReturnStep(charged, reportedRa, reportedDec, nearOriginToleranceArcseconds, now);
        if (next.Gate.Disposition != GateDisposition.Passed) return new(next.Gate);
        return new(GateResult.Pass("G3_GUIDING_RECOVERY_RETURN_RESERVED",
            "已在原账本中预留导星位置差及完整回程；仅允许先回原点，再取新图重新定位。"), charged, delta, returnActions);
    }
}
