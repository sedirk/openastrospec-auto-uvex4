using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

internal sealed partial class RealObservationStageRunner
{
    private bool targetStrategyPublished;
    private EquatorialTarget? targetStrategyTarget;
    private TargetAcquisitionBranch? activeTargetBranch;

    private static TargetAcquisitionStrategyDecision ResolveTargetStrategy(ObservationContext context) =>
        TargetAcquisitionStrategyPolicy.Resolve(context.Plan.TargetObservability,
            context.Plan.Target, context.Plan.CatalogMetadata);

    private async Task<GateResult> PublishTargetStrategyAsync(ObservationContext context, CancellationToken cancellationToken)
    {
        var strategy = ResolveTargetStrategy(context);
        targetStrategyTarget = context.Plan.Target;
        if (targetStrategyPublished) return strategy.Gate;
        await PublishRunJsonEvidenceAsync("target-acquisition-strategy", "本轮目标识别策略与条件优先级",
            new { policyVersion = TargetAcquisitionStrategyPolicy.Version, strategy, context.Plan.CatalogMetadata,
                motionBudgetReset = false, targetCoordinatesChanged = false }, null, cancellationToken,
            new Dictionary<string, string> { ["code"] = strategy.Gate.Code, ["message"] = strategy.Summary,
                ["requestedClass"] = strategy.RequestedClass.ToString(), ["effectiveClass"] = strategy.EffectiveClass.ToString(),
                ["targetName"] = context.Plan.Target.Name, ["catalogId"] = context.Plan.Target.CatalogId,
                ["observationRunId"] = context.Plan.ObservationRunId }).ConfigureAwait(false);
        host.PublishGate(ObservationStage.ValidateNightSetup, strategy.Gate with { Message = strategy.Summary });
        Report(strategy.Summary);
        targetStrategyPublished = true;
        return strategy.Gate;
    }

    private async Task PublishTargetBranchAsync(ObservationContext context, TargetAcquisitionBranch branch,
        string code, string reason, string? sourcePath, CancellationToken cancellationToken)
    {
        var description = TargetAcquisitionStrategyPolicy.Describe(branch);
        activeTargetBranch = branch;
        targetStrategyTarget = context.Plan.Target;
        var message = $"识别分支【{description.Label}】：{reason}";
        await PublishRunJsonEvidenceAsync("target-acquisition-branch", message,
            new { policyVersion = TargetAcquisitionStrategyPolicy.Version, branch = branch.ToString(), code, reason,
                configuredStrategy = context.Plan.TargetObservability.ToString(),
                effectiveClass = ResolveTargetStrategy(context).EffectiveClass.ToString(),
                originalMotionBudgetRetained = true, originalTargetRetained = true }, sourcePath, cancellationToken,
            new Dictionary<string, string> { ["code"] = code, ["branch"] = branch.ToString(),
                ["branchLabel"] = description.Label, ["message"] = message,
                ["stage"] = host.Dashboard.Run.CurrentStage?.ToString() ?? string.Empty,
                ["targetName"] = context.Plan.Target.Name, ["catalogId"] = context.Plan.Target.CatalogId,
                ["observationRunId"] = context.Plan.ObservationRunId }).ConfigureAwait(false);
        // Branch progress is telemetry, not a new passing quality gate. In
        // particular a failed branch must never overwrite its actual failure.
        Report(message);
    }

    private async Task<TargetIdentification> IdentifyTargetWithStrategyAsync(ObservationContext context,
        MonochromeFrame frame, IReadOnlyList<StarCandidate> candidates, PixelPoint prediction,
        double recognitionRadius, string sourcePath, CancellationToken cancellationToken)
    {
        var decision = TargetAcquisitionStrategyPolicy.IdentifyFromVerifiedWcs(
            ResolveTargetStrategy(context), frame, candidates, prediction, recognitionRadius);
        var code = decision.Identification.Gate.Disposition != GateDisposition.Passed ? "TARGET_BRANCH_BLOCKED" :
            decision.IsFallback ? "TARGET_BRANCH_FALLBACK" : "TARGET_BRANCH_SELECTED";
        await PublishTargetBranchAsync(context, decision.Branch, code, decision.Reason, sourcePath, cancellationToken).ConfigureAwait(false);
        return decision.Identification;
    }

    private void PublishCompletedTargetBranch(G3FieldState field)
    {
        var branch = field.GhostAssistance is { Result.Decision: GhostAssistanceDecision.UseCalibratedAuxiliaryEstimate }
            ? TargetAcquisitionBranch.CalibratedGhost
            : field.BrightTargetAuthority is not null ? TargetAcquisitionBranch.CalibratedBrightWings
            : !string.IsNullOrWhiteSpace(field.TargetIdentification.BoundShortPositionEvidencePath) ? TargetAcquisitionBranch.ShortExposureSep
            : field.TargetIdentification.CatalogPositionRefinedFromSameFrame ? TargetAcquisitionBranch.DirectStellarPosition
            : field.TargetIdentification.Authority == TargetIdentificationAuthority.CatalogWcsProjection ? TargetAcquisitionBranch.CatalogWcsGeometry
            : TargetAcquisitionBranch.DirectStellarPosition;
        var label = TargetAcquisitionStrategyPolicy.Describe(branch).Label;
        var message = $"识别分支【{label}】已完成目标取场；接下来仍验证实时狭缝与导星，不把取场成功等同于精确入缝。";
        host.PublishEvidence("target-acquisition-branch", field.FramePath, metadata: new Dictionary<string, string>
        {
            ["code"] = "TARGET_BRANCH_PASSED", ["branch"] = branch.ToString(), ["branchLabel"] = label,
            ["stage"] = host.Dashboard.Run.CurrentStage?.ToString() ?? string.Empty,
            ["message"] = message, ["targetIdentityAuthority"] = field.TargetIdentification.Authority.ToString(),
            ["targetName"] = targetStrategyTarget?.Name ?? string.Empty,
            ["catalogId"] = targetStrategyTarget?.CatalogId ?? string.Empty,
            ["observationRunId"] = observationRunId ?? string.Empty,
        });
    }
}
