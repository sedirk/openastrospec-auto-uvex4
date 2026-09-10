using UvexAdv.Observatory;
using UvexAdv.Phd2;

namespace UvexAdv.Nina.Plugin;

internal sealed partial class RealObservationStageRunner
{
    private Phd2DependencyRebuildStopProof? automaticRebuildStopProof;

    private async Task<GateResult> ReturnAfterOwnedGuidingBeforeReacquisitionAsync(
        ObservationContext context, CancellationToken cancellationToken)
    {
        var proof = automaticRebuildStopProof;
        automaticRebuildStopProof = null; // Single use; never restart a return by replaying this receipt.
        if (proof is null || durableG3AcquisitionMotion is not { Phase: G3AcquisitionMotionPhase.SettledBudgetLedger } state)
            return GateResult.Pass("G3_GUIDING_RECOVERY_NO_OWNED_HANDOFF", "没有待处理的本轮导星接续；沿原有恢复规则检查。");
        if (!proof.IsCurrent(phd2.Snapshot))
            return GateResult.Unknown("G3_GUIDING_RECOVERY_STOP_CHANGED", "本轮导星停止证明已失效；未发送回程。");
        var identity = ValidateG3AcquisitionMotionIdentity(context, state);
        if (identity.Disposition != GateDisposition.Passed) return identity;
        var loaded = await G3AcquisitionMotionStore.LoadAsync(G3AcquisitionMotionPath(state.ObservationRunId), cancellationToken).ConfigureAwait(false);
        if (loaded.Error is not null || loaded.State != state)
            return GateResult.Unknown("G3_GUIDING_RECOVERY_LEDGER_CHANGED", "回程前原运动账本校验失败或已改变；未覆盖账本。");
        var owner = await phd2.ValidateIdentityAsync(PhdIdentityRequirement(), cancellationToken).ConfigureAwait(false);
        if (!owner.IsValid)
            return GateResult.Unknown("G3_GUIDING_RECOVERY_OWNER_CHANGED", "回程前 PHD2 设备身份已改变；未发送运动。");
        await RequireImmediatePhysicalActionGatesAsync(context, cancellationToken).ConfigureAwait(false);
        var mount = ValidateG3SearchMountState(state.PierSide);
        if (mount.Disposition != GateDisposition.Passed) return mount;
        var reported = telescopeMediator.GetCurrentPosition();
        var nearTolerance = G3AcquisitionMotionPlanner.ComputeStableNearOriginToleranceArcseconds(
            state, configuration.G3.WcsFreshSolveAuthorizationResidualArcseconds);
        var plan = G3GuidingRecoveryReturnPolicy.Plan(state, context.Plan.ObservationRunId,
            NormalizeDegrees(reported.RADegrees), reported.Dec, reported.Epoch.ToString(), state.PierSide,
            proof.IsCurrent(phd2.Snapshot), MountMotionFamilyHandoffToleranceArcseconds, nearTolerance, DateTimeOffset.UtcNow);
        if (plan.Gate.Disposition != GateDisposition.Passed || plan.ChargedLedger is null) return plan.Gate;
        await PublishRunJsonEvidenceAsync("g3-guiding-recovery-return-reserved",
            "Owned guide stop requires a charged return before fresh acquisition",
            new { before = state, charged = plan.ChargedLedger, plan.ContinuityDeltaArcseconds,
                plan.ReservedReturnActions, proof.EvidencePath, sourceAuthority = "owned-guide-stop-and-durable-origin",
                newSearchAuthorized = false, durableMotionBudgetsReset = false },
            proof.EvidencePath, cancellationToken).ConfigureAwait(false);
        await PersistG3AcquisitionMotionAsync(plan.ChargedLedger, CancellationToken.None).ConfigureAwait(false);
        cumulativeCorrectionDegrees = Math.Max(cumulativeCorrectionDegrees, plan.ChargedLedger.CumulativeMotionArcseconds / 3600d);
        correctionAttempts = Math.Max(correctionAttempts, plan.ChargedLedger.CorrectionAttempts);
        Report($"导星后位置与粗定位账本相差 {plan.ContinuityDeltaArcseconds:F1}″；已预留原预算，先回原搜索起点再重新定位");
        var returned = await ReturnDurableG3AcquisitionToOriginAsync(
            context, plan.ChargedLedger, cancellationToken, proof).ConfigureAwait(false);
        cumulativeCorrectionDegrees = Math.Max(cumulativeCorrectionDegrees, returned.State.CumulativeMotionArcseconds / 3600d);
        correctionAttempts = Math.Max(correctionAttempts, returned.State.CorrectionAttempts);
        await PublishRunJsonEvidenceAsync("g3-guiding-recovery-return-result",
            "Measured return result before any new full-frame acquisition",
            new { returned.ReturnedToOrigin, returned.Message, state = returned.State,
                freshAcquisitionAllowed = returned.ReturnedToOrigin, durableMotionBudgetsReset = false },
            proof.EvidencePath, cancellationToken).ConfigureAwait(false);
        return returned.ReturnedToOrigin
            ? GateResult.Pass("G3_GUIDING_RECOVERY_ORIGIN_CONFIRMED", "已实测返回原搜索起点；下一步重拍定位，旧目标证据不复用。")
            : GateResult.Unknown("G3_GUIDING_RECOVERY_RETURN_UNCONFIRMED", $"导星后回原点未确认；不开始搜索：{returned.Message}");
    }
}
