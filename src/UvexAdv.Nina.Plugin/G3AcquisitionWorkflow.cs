using UvexAdv.Observatory;
using static UvexAdv.Nina.Plugin.ObservationWorkflowResults;

namespace UvexAdv.Nina.Plugin;

// The only acquisition branch dispatcher. Return obligations are reconciled
// before capture; a field-in-frame is never silently promoted to slit placement.
internal sealed class G3AcquisitionWorkflow(G3RunConfiguration configuration, IG3AcquisitionPort port)
{

    internal async Task<StageResult> AcquireAsync(
        ObservationContext context,
        CancellationToken cancellationToken,
        bool allowChargedCurrentPositionHandoff = false)
    {
        var interlock = await port.EvaluateInterlocksAsync(context, cancellationToken).ConfigureAwait(false);
        if (interlock.Disposition != GateDisposition.Passed) return new StageResult(interlock);
        port.EnsureFineAcquisitionClockStarted();
        if (configuration.FocalLengthMillimeters <= 0 || configuration.PixelSizeMicrometers <= 0)
        {
            return Attention(ObservationStage.AcquireG3SlitField, "G3_OPTICS_UNCOMMISSIONED", "G3 focal length and pixel size must be measured and configured; plate scale will not be guessed.");
        }
        if (configuration.ExpectedWcsFlipped)
        {
            return Attention(ObservationStage.AcquireG3SlitField, "G3_FLIPPED_WCS_UNIMPLEMENTED", "A flipped G3 WCS is configured, but its detector-axis parity mapping has not been commissioned in this adapter.");
        }

        var pendingReturn = await port.CompletePendingG3SearchReturnAsync(context, cancellationToken).ConfigureAwait(false);
        if (pendingReturn is not null) return pendingReturn;

        // All entry points (outer recovery, exact-lock return, native
        // recalibration and LostLock) converge here before ANY new frame.
        var guidingReturn = await port.ReturnAfterOwnedGuidingBeforeReacquisitionAsync(context, cancellationToken).ConfigureAwait(false);
        if (guidingReturn.Disposition != GateDisposition.Passed) return new StageResult(guidingReturn);

        var transferEvidencePath = await port.EnsureWideToSlitTransferSkippedEvidenceAsync(
            context,
            cancellationToken).ConfigureAwait(false);

        var catalogPositionOnly = port.UsesCatalogPositionOnly(context);
        await port.PublishTargetBranchAsync(context,
            catalogPositionOnly ? TargetAcquisitionBranch.CatalogWcsGeometry : TargetAcquisitionBranch.DirectStellarPosition,
            "TARGET_BRANCH_SELECTED", "先取得本轮导星相机正式 WCS；只有当前帧证据满足条件才使用后续分支。", null, cancellationToken).ConfigureAwait(false);
        var field = await port.CaptureAndAnalyzeG3WithSolveLadderAsync(context, cancellationToken).ConfigureAwait(false);
        port.RememberField(field);
        if (field.Gate.Disposition == GateDisposition.Passed)
        {
            var placementPreset = port.PlacementPreset;
            var coarseResidualPixels = field.TargetIdentification.Target is { } target &&
                                       field.SlitDetection.Gate.Disposition == GateDisposition.Passed
                ? PixelDistance(target.Centroid, field.SlitDetection.Geometry.AcquisitionPoint)
                : double.NaN;
            if (placementPreset is not null && G3WcsRecoveryPolicy.NeedsCoarseCentering(
                field.Gate, field.Solve?.Result.Success == true && field.Solve.Result.Coordinates is not null,
                coarseResidualPixels + field.TargetIdentification.CatalogPositionSpreadPixels, port.HandoffResidualPixels(field, placementPreset)))
            {
                // "Inside the G3 frame" is not the coarse-acquisition goal.
                // The validated live route uses N.I.N.A. for the large WCS
                // correction all the way to the slit window, reserving PHD2
                // lock shifts for the final small residual.
                return await port.RunG3WcsCenteringAsync(
                    context,
                    field,
                    transferEvidencePath,
                    cancellationToken,
                    allowChargedCurrentPositionHandoff).ConfigureAwait(false);
            }
            return port.AcceptField(field, transferEvidencePath);
        }
        if (field.Gate.Code == "G3_SOLVED_TARGET_OUTSIDE" && field.Solve?.Result.Success == true)
        {
            return await port.RunG3WcsCenteringAsync(
                context,
                field,
                transferEvidencePath,
                cancellationToken,
                allowChargedCurrentPositionHandoff).ConfigureAwait(false);
        }
        if (!TargetAcquisitionStrategyPolicy.MayTryBoundedNeighbor(field.Gate))
        {
            await port.PublishTargetBranchAsync(context,
                port.ActiveTargetBranch ?? (catalogPositionOnly ? TargetAcquisitionBranch.CatalogWcsGeometry : TargetAcquisitionBranch.DirectStellarPosition),
                "TARGET_BRANCH_BLOCKED", $"{field.Gate.Code}: {field.Gate.Message} 此失败没有兼容的自动回退，不切换目标或重置预算。",
                field.FramePath, cancellationToken).ConfigureAwait(false);
            return new StageResult(
                field.Gate,
                field.FramePath,
                new Dictionary<string, string>
                {
                    ["wideToSlitTransferMode"] = WideToSlitTransferMode.Skip.ToString(),
                    ["transferOutcome"] = "TransferSkipped",
                    ["transferEvidencePath"] = transferEvidencePath,
                    ["slitIdentityGate"] = field.SlitIdentity?.Gate.Code ?? "SLIT_LED_IDENTITY_NOT_RECORDED",
                    ["slitIdentityEvidencePath"] = field.SlitIdentityEvidencePath ?? string.Empty,
                });
        }

        await port.PublishTargetBranchAsync(context, TargetAcquisitionBranch.BoundedNeighborWcs,
            "TARGET_BRANCH_FALLBACK", $"直接取场未通过（{field.Gate.Code}）；按原限制尝试邻场解算，所有新动作仍计入原账本。",
            field.FramePath, cancellationToken).ConfigureAwait(false);
        return await port.RunBoundedG3LocalSearchAsync(
            context,
            field,
            transferEvidencePath,
            cancellationToken,
            allowChargedCurrentPositionHandoff).ConfigureAwait(false);
    }
    private static double PixelDistance(PixelPoint a, PixelPoint b) =>
        Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
}
