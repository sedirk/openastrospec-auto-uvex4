using UvexAdv.Observatory;
using UvexAdv.Phd2;

namespace UvexAdv.Nina.Plugin;

internal sealed partial class RealObservationStageRunner
{
    private Phd2DependencyRebuildStopProof? automaticRebuildStopProof;
    private (string Run, string Lineage, Phd2DependencyRebuildStopProof Proof)? g3StoppedHandoff;

    private async Task<GateResult> ReturnAfterOwnedGuidingBeforeReacquisitionAsync(
        ObservationContext context, CancellationToken cancellationToken)
    {
        var selected = G3GuidingRecoveryReturnPolicy.SelectOwnedStopProof(automaticRebuildStopProof,
            g3StoppedHandoff, context.Plan.ObservationRunId, durableG3AcquisitionMotion, phd2.Snapshot);
        if (selected.Gate.Disposition != GateDisposition.Passed) return selected.Gate;
        var proof = selected.Proof;
        automaticRebuildStopProof = null; // Single use; never restart a return by replaying this receipt.
        g3StoppedHandoff = null; // Re-established below only after every current boundary check.
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
        if (mount.Disposition != GateDisposition.Passed)
            return G3GuidingRecoveryReturnPolicy.FailureGate(mount, mount.Message);
        var reported = telescopeMediator.GetCurrentPosition();
        var nearTolerance = G3AcquisitionMotionPlanner.ComputeStableNearOriginToleranceArcseconds(
            state, configuration.G3.WcsFreshSolveAuthorizationResidualArcseconds);
        var plan = G3GuidingRecoveryReturnPolicy.Plan(state, context.Plan.ObservationRunId,
            NormalizeDegrees(reported.RADegrees), reported.Dec, reported.Epoch.ToString(), state.PierSide,
            proof.IsCurrent(phd2.Snapshot), MountMotionFamilyHandoffToleranceArcseconds, nearTolerance, DateTimeOffset.UtcNow,
            preferFreshCurrentPositionHandoff: true);
        if (plan.Gate.Disposition != GateDisposition.Passed)
        {
            await PublishRunJsonEvidenceAsync("g3-guiding-recovery-return-declined",
                "Recovery return declined without motion or ledger reset",
                new { before = state, plan.Gate, plan.ContinuityDeltaArcseconds, plan.ReservedReturnActions,
                    reportedRaDegrees = NormalizeDegrees(reported.RADegrees), reportedDeclinationDegrees = reported.Dec,
                    proof.EvidencePath, returnCommandSent = false, durableMotionBudgetsReset = false,
                    requiresIndependentTerminalBoundary = true },
                proof.EvidencePath, cancellationToken).ConfigureAwait(false);
            return plan.Gate;
        }
        return await G3GuidingRecoveryReturnPolicy.DispatchAsync(plan, async () =>
        {
            // Retain stopped accounting continuity through an OWNED native
            // single-frame acquisition, not permission to replay this receipt.
            // No position adoption, motion, WCS claim or debit happens here.
            if (plan.CaptureCurrentPositionFirst)
                await PublishRunJsonEvidenceAsync("g3-guiding-recovery-current-capture-reserved",
                    "Owned guide stop retains current pointing for fresh bound acquisition before coarse ledger handoff",
                    new { before = state, plan.Gate, plan.ContinuityDeltaArcseconds, plan.ReservedReturnActions,
                        proof.EvidencePath, actualHandoffDebitPending = true, freshMountBoundImageRequired = true,
                        formalWcsEstablished = false, returnCommandSent = false, durableMotionBudgetsReset = false },
                    proof.EvidencePath, cancellationToken).ConfigureAwait(false);
            if (!proof.IsCurrent(phd2.Snapshot))
                return GateResult.Unknown("G3_GUIDING_RECOVERY_STOP_CHANGED", "新图交接前本轮停止证明已改变；不采集或收养位置。");
            g3StoppedHandoff = (context.Plan.ObservationRunId, state.BudgetLineageId, proof);
            return plan.Gate;
        }, async chargedLedger =>
        {
            await PublishRunJsonEvidenceAsync("g3-guiding-recovery-return-reserved",
                "Owned guide stop requires a charged return before fresh acquisition",
                new { before = state, charged = chargedLedger, plan.ContinuityDeltaArcseconds,
                    plan.ReservedReturnActions, proof.EvidencePath, sourceAuthority = "owned-guide-stop-and-durable-origin",
                    newSearchAuthorized = false, durableMotionBudgetsReset = false },
                proof.EvidencePath, cancellationToken).ConfigureAwait(false);
            await PersistG3AcquisitionMotionAsync(chargedLedger, CancellationToken.None).ConfigureAwait(false);
            cumulativeCorrectionDegrees = Math.Max(cumulativeCorrectionDegrees, chargedLedger.CumulativeMotionArcseconds / 3600d);
            correctionAttempts = Math.Max(correctionAttempts, chargedLedger.CorrectionAttempts);
            Report($"导星后位置与粗定位账本相差 {plan.ContinuityDeltaArcseconds:F1}″；已预留原预算，先回原搜索起点再重新定位");
            var returned = await ReturnDurableG3AcquisitionToOriginAsync(
                context, chargedLedger, cancellationToken, proof).ConfigureAwait(false);
            cumulativeCorrectionDegrees = Math.Max(cumulativeCorrectionDegrees, returned.State.CumulativeMotionArcseconds / 3600d);
            correctionAttempts = Math.Max(correctionAttempts, returned.State.CorrectionAttempts);
            await PublishRunJsonEvidenceAsync("g3-guiding-recovery-return-result",
                "Measured return result before any new full-frame acquisition",
                new { returned.ReturnedToOrigin, returned.Message, state = returned.State,
                    freshAcquisitionAllowed = returned.ReturnedToOrigin, durableMotionBudgetsReset = false },
                proof.EvidencePath, cancellationToken).ConfigureAwait(false);
            return returned.ReturnedToOrigin
                ? GateResult.Pass("G3_GUIDING_RECOVERY_ORIGIN_CONFIRMED", "已实测返回原搜索起点；下一步重拍定位，旧目标证据不复用。")
                : G3GuidingRecoveryReturnPolicy.FailureGate(returned.FailureGate, returned.Message);
        }).ConfigureAwait(false);
    }

    private async Task<GateResult> FinalizeOwnedStoppedG3HandoffAsync(
        ObservationContext context, CancellationToken cancellationToken)
    {
        if (g3StoppedHandoff is not { } handoff ||
            durableG3AcquisitionMotion is not { Phase: G3AcquisitionMotionPhase.SettledBudgetLedger } ledger ||
            handoff.Run != context.Plan.ObservationRunId || handoff.Run != ledger.ObservationRunId ||
            handoff.Lineage != ledger.BudgetLineageId)
            return GateResult.Unknown("G3_GUIDING_RECOVERY_LEDGER_CHANGED",
                "成功取场后的停止交接不再匹配本轮 settled 账本；不启动新导星会话。");
        if (!handoff.Proof.IsCurrent(phd2.Snapshot))
            return GateResult.Unknown("G3_GUIDING_RECOVERY_STOP_CHANGED", "取场后原停止证明已改变；不消费或续用旧接续。");
        var identity = ValidateG3AcquisitionMotionIdentity(context, ledger);
        if (identity.Disposition != GateDisposition.Passed) return identity;
        var loaded = await G3AcquisitionMotionStore.LoadAsync(
            G3AcquisitionMotionPath(ledger.ObservationRunId), cancellationToken).ConfigureAwait(false);
        if (loaded.Error is not null || loaded.State != ledger)
            return GateResult.Unknown("G3_GUIDING_RECOVERY_LEDGER_CHANGED", "取场后的原账本已改变或无法独立验证；不覆盖账本。");
        var owner = await phd2.ValidateIdentityAsync(PhdIdentityRequirement(), cancellationToken).ConfigureAwait(false);
        if (!owner.IsValid)
            return GateResult.Unknown("G3_GUIDING_RECOVERY_OWNER_CHANGED", "取场后 PHD2 所有者身份不匹配；不开始精调。");
        await RequireImmediatePhysicalActionGatesAsync(context, cancellationToken).ConfigureAwait(false);
        var quiet = ValidateG3SearchMountState(ledger.PierSide);
        if (quiet.Disposition != GateDisposition.Passed) return quiet;
        if (lastG3Field is not { Gate.Disposition: GateDisposition.Passed } field)
            return GateResult.Unknown("G3_GUIDING_RECOVERY_FRESH_FIELD_REQUIRED", "停止交接缺少本轮成功新图；不能仅消费旧停止凭据。");

        // Always hash/check the fresh image independently, even at the old
        // lineage origin where an ordinary completed return needs no new WCS.
        var binding = await ValidateG3FieldMountBindingForMotionAsync(context, field, cancellationToken).ConfigureAwait(false);
        if (binding.Disposition != GateDisposition.Passed) return binding;
        cancellationToken.ThrowIfCancellationRequested();
        if (!handoff.Proof.IsCurrent(phd2.Snapshot))
            return GateResult.Unknown("G3_GUIDING_RECOVERY_STOP_CHANGED", "新图绑定核验期间原停止证明已改变；不接续账本。");
        quiet = ValidateG3SearchMountState(ledger.PierSide);
        if (quiet.Disposition != GateDisposition.Passed) return quiet;
        var current = telescopeMediator.GetCurrentPosition();
        // Same family and exact original ceilings/evidence only. No optional
        // additional/attested budget is passed; Begin conservatively accounts
        // the actual new endpoint and consumes the receipt without movement.
        await BeginG3AcquisitionMotionAsync(context, ledger.Kind, current, ledger.PierSide,
            ledger.MaximumSingleCorrectionArcseconds, ledger.MaximumRadiusArcseconds,
            ledger.MaximumCumulativeMotionArcseconds, ledger.MaximumCorrectionAttempts,
            TimeSpan.FromSeconds(ledger.MaximumElapsedSeconds), ledger.DeclaredEvidencePath,
            cancellationToken, allowChargedCurrentPositionHandoff: true,
            freshMountBoundHandoffAuthority: field).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!handoff.Proof.IsCurrent(phd2.Snapshot))
            return GateResult.Unknown("G3_GUIDING_RECOVERY_STOP_CHANGED", "账本接续完成时出现新的 PHD2 会话；不放行精调或曝光。");
        return GateResult.Pass("G3_GUIDING_RECOVERY_FRESH_HANDOFF_COMPLETED",
            "新图及停止/静止已独立验证，当前位置按原账本接续并消费停止凭据；后续导星建立全新会话，未发运动或伪造 WCS。");
    }
}
