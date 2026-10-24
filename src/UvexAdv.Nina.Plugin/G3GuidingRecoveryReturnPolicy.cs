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

    // Only the native single-frame primitive may run under this receipt. Its
    // client preserves the stopped guide epoch for its locally owned capture;
    // loop/save, preview takeover and any external epoch change invalidate it.
    // Never retry or manufacture a replacement receipt on failure. A native
    // capture timeout is NOT a disposable-image failure: the client's best-
    // effort stop is not a confirmed end of acquisition, even if AppState
    // stayed Stopped throughout the native exposure.
    internal async Task<T> CaptureAsync<T>(Func<Phd2StateSnapshot> readState,
        Func<CancellationToken, Task<T>> nativeCapture, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(readState);
        ArgumentNullException.ThrowIfNull(nativeCapture);
        cancellationToken.ThrowIfCancellationRequested();
        RequireCurrent();
        T result;
        try { result = await nativeCapture(cancellationToken).ConfigureAwait(false); }
        catch (Phd2CommandTimeoutException ex)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new PhysicalActionGateException(GateResult.Unknown(Phd2StageFailurePolicy.NativeFrameTimeout,
                $"Owned single-frame handoff timed out; do not advance the exposure ladder or issue motion before independent stop confirmation. {ex.Message}"));
        }
        cancellationToken.ThrowIfCancellationRequested();
        RequireCurrent();
        return result;

        void RequireCurrent()
        {
            if (!IsCurrent(readState()))
                throw new PhysicalActionGateException(GateResult.Unknown("G3_GUIDING_RECOVERY_STOP_CHANGED",
                    "本轮停止/所有者/导星纪元在原生交接采集前后改变；新图不授予位置交接或运动权限。"));
        }
    }
}

internal sealed record G3GuidingRecoveryReturnPlan(
    GateResult Gate, G3AcquisitionMotionState? ChargedLedger = null,
    double ContinuityDeltaArcseconds = 0, int ReservedReturnActions = 0,
    bool CaptureCurrentPositionFirst = false);

internal sealed record G3OwnedStopSelection(GateResult Gate, Phd2DependencyRebuildStopProof? Proof = null);

internal static class G3GuidingRecoveryReturnPolicy
{
    // A bounded read-only acquisition retry retains the exact in-process stop
    // object, not a new epoch, receipt, deadline or return attempt. Conflicting
    // new receipts cannot silently replace this unfinished responsibility.
    internal static G3OwnedStopSelection SelectOwnedStopProof(
        Phd2DependencyRebuildStopProof? newlyStopped,
        (string Run, string Lineage, Phd2DependencyRebuildStopProof Proof)? pending,
        string runId, G3AcquisitionMotionState? ledger, Phd2StateSnapshot current)
    {
        if (pending is not { } handoff)
            return new(GateResult.Pass("G3_GUIDING_RECOVERY_STOP_SELECTED", "沿用本次停止凭据，不创建新凭据。"), newlyStopped);
        if (newlyStopped is not null && !ReferenceEquals(newlyStopped, handoff.Proof))
            return new(GateResult.Unknown("G3_GUIDING_RECOVERY_STOP_CONFLICT",
                "已有未消费的本轮停止交接，又出现另一停止凭据；不覆盖旧责任或开始采集。"));
        if (ledger is not { Phase: G3AcquisitionMotionPhase.SettledBudgetLedger } ||
            ledger.Validate().Count != 0 || handoff.Run != runId || handoff.Run != ledger.ObservationRunId ||
            handoff.Lineage != ledger.BudgetLineageId)
            return new(GateResult.Unknown("G3_GUIDING_RECOVERY_LEDGER_CHANGED",
                "新帧重试的原运行或 settled lineage 已改变；不丢弃旧交接或重新发放预算。"));
        if (!handoff.Proof.IsCurrent(current))
            return new(GateResult.Unknown("G3_GUIDING_RECOVERY_STOP_CHANGED",
                "新帧重试前原停止/所有者/导星纪元已改变；不刷新停止凭据。"));
        return new(GateResult.Pass("G3_GUIDING_RECOVERY_SAME_STOP_RETAINED",
            "只读重拍保留同一个本地停止对象，并重新核验身份、安全与原回程预留；不刷新纪元或预算。"), handoff.Proof);
    }

    // A direct successful acquisition may not need a coarse-motion family.
    // It still must consume the owned-stop receipt and account for its actual
    // displacement before PHD2 is allowed to create a new guide epoch.
    internal static async Task<StageResult> FinalizeAcceptedFieldAsync(StageResult result,
        bool hasPendingOwnedHandoff, Func<Task<GateResult>> finalize)
    {
        if (!result.CanAdvance || !hasPendingOwnedHandoff) return result;
        var gate = await finalize().ConfigureAwait(false);
        return gate.Disposition == GateDisposition.Passed ? result : result with { Gate = gate };
    }

    // The production recovery caller has already checked the original owner,
    // durable record and immediate physical gates. This dispatch makes the
    // accounting-only/current-capture branch and the old charged return branch
    // mutually exclusive; neither delegate may run for a refused plan.
    internal static Task<GateResult> DispatchAsync(G3GuidingRecoveryReturnPlan plan,
        Func<Task<GateResult>> prepareCurrentCapture,
        Func<G3AcquisitionMotionState, Task<GateResult>> returnToOrigin) =>
        plan.Gate.Disposition != GateDisposition.Passed ? Task.FromResult(plan.Gate) :
        plan.ChargedLedger is { } charged ? returnToOrigin(charged) : prepareCurrentCapture();

    // The active native meridian boundary takes precedence in the mount adapter.
    // Without that handoff, a KNOWN changed side invalidates this target's old
    // geometry, not the whole night. This is only classification: the terminal
    // boundary must still join every owner, confirm stop and retire old ledgers.
    // Unknown side, motion, tracking, identity and weather failures stay hard.
    internal static GateResult FailureGate(GateResult? failure, string detail)
    {
        var message = $"导星后回原点未确认；不开始搜索：{detail}";
        return failure is { Code: "G3_SEARCH_PIER_SIDE_CHANGED", Disposition: GateDisposition.Failed }
            ? GateResult.TargetQuality("G3_GUIDING_RECOVERY_PIER_CHANGED",
                $"{message} 已知换侧使旧回程失效；停止与账本归档确认后按目标失败策略处理，不声称回位成功。",
                new Dictionary<string, double> { ["returnedToOrigin"] = 0 })
            : failure is { Disposition: not GateDisposition.Passed }
                ? failure with { Message = message }
                : GateResult.Unknown("G3_GUIDING_RECOVERY_RETURN_UNCONFIRMED", message);
    }

    internal static G3GuidingRecoveryReturnPlan Plan(
        G3AcquisitionMotionState state, string runId, double reportedRa, double reportedDec,
        string epoch, string pierSide, bool ownedGuideStopConfirmed,
        double handoffToleranceArcseconds, double nearOriginToleranceArcseconds, DateTimeOffset now,
        bool preferFreshCurrentPositionHandoff = false)
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

        if (preferFreshCurrentPositionHandoff)
        {
            // ADR-0040: a confirmed owned guide stop need not physically erase
            // useful current pointing before taking its new immutable image.
            // This is a read-only preview of the EXISTING measured-handoff
            // debit and complete return reserve. Do not persist/adopt its
            // returned ledger: actual fresh image binding and the later debit
            // are still required at BeginG3AcquisitionMotionAsync.
            var preview = G3MeasuredHandoffPolicy.Charge(state, delta, radius, now);
            if (preview.Gate.Disposition == GateDisposition.Passed)
                return new(GateResult.Pass("G3_GUIDING_RECOVERY_CURRENT_CAPTURE_RESERVED",
                    "本轮导星已确认停止，原账本可容纳当前位置交接及完整回程；先取本指向新图，待新图绑定后再实际扣账，不预填 WCS 或扩大预算。",
                    preview.Gate.Metrics), ContinuityDeltaArcseconds: delta,
                    ReservedReturnActions: checked((int)preview.Gate.Metrics!["reservedReturnActions"]),
                    CaptureCurrentPositionFirst: true);
            // The current-frame path is an optimization, not permission to
            // discard the original return route or its stricter physical gates.
            // If its reserve does not fit, evaluate the legacy return below.
        }

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
        var requiredActions = charged.CorrectionAttempts + (long)returnActions;
        var requiredMotion = charged.CumulativeMotionArcseconds + returnActions * state.MaximumSingleCorrectionArcseconds;
        var elapsed = (now - state.MotionClockStartedUtc).TotalSeconds;
        var requiredSeconds = elapsed + (1d + returnActions) * state.WorstCaseActionSeconds;
        if (requiredActions > state.MaximumCorrectionAttempts ||
            requiredMotion > state.MaximumCumulativeMotionArcseconds || requiredSeconds > state.MaximumElapsedSeconds)
            // Numeric exhaustion is local to this target, not authority to execute a
            // return or reset its ledger. The native boundary must independently join,
            // confirm all owners stopped and retire the ledger before any next target.
            return new(GateResult.TargetQuality("G3_GUIDING_RECOVERY_RETURN_RESERVE_LIMIT",
                "原动作、位移或时间预算不足以覆盖导星位置差及完整回程；未发送回程、未重置预算。停止与旧账本归档确认后按本目标失败策略处理。",
                new Dictionary<string, double>
                {
                    ["requiredActions"] = requiredActions, ["maximumActions"] = state.MaximumCorrectionAttempts,
                    ["requiredMotionArcseconds"] = requiredMotion, ["maximumMotionArcseconds"] = state.MaximumCumulativeMotionArcseconds,
                    ["elapsedSeconds"] = elapsed, ["requiredSeconds"] = requiredSeconds, ["maximumElapsedSeconds"] = state.MaximumElapsedSeconds,
                    ["continuityDeltaArcseconds"] = delta, ["radiusArcseconds"] = radius, ["returnActions"] = returnActions,
                }), ContinuityDeltaArcseconds: delta, ReservedReturnActions: returnActions);
        var next = G3AcquisitionMotionPlanner.PlanNextReturnStep(charged, reportedRa, reportedDec, nearOriginToleranceArcseconds, now);
        if (next.Gate.Disposition != GateDisposition.Passed) return new(next.Gate);
        return new(GateResult.Pass("G3_GUIDING_RECOVERY_RETURN_RESERVED",
            "已在原账本中预留导星位置差及完整回程；仅允许先回原点，再取新图重新定位。"), charged, delta, returnActions);
    }
}
