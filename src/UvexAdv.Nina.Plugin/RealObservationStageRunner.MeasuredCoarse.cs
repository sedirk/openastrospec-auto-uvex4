using NINA.Astrometry;
using NINA.PlateSolving;
using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

internal sealed partial class RealObservationStageRunner
{
    private bool MeasuredCoarseAnchorUsable(G3WcsMotionPrediction prediction)
    {
        if (prediction.FormalAnchor is not { IsFormal: true, Result.Success: true } anchor ||
            anchor.Result.Coordinates is null || prediction.SourceTiming is null ||
            durableG3AcquisitionMotion is not { Phase: G3AcquisitionMotionPhase.AwaitingFreshSolve } ledger)
            return false;
        return G3MeasuredCoarseRecoveryPolicy.AnchorUsable(true, ledger.MeasuredPixelCorrectionAttempts,
            (DateTimeOffset.UtcNow - prediction.SourceTiming.CaptureCompletedUtc).TotalSeconds,
            G3AcquisitionMotionPlanner.AngularSeparationArcseconds(anchor.Result.Coordinates.RADegrees,
                anchor.Result.Coordinates.Dec, prediction.EstimatedFieldCenter.RADegrees, prediction.EstimatedFieldCenter.Dec),
            anchor.Result.Pixscale);
    }

    // A local tangent model, explicitly NOT a formal solve of this short image.
    // The old centre is not transported as current position: translation comes
    // from this independently measured catalogue-associated target alone.
    internal static PlateSolveResult MeasuredCoarseLocalModel(PlateSolveEvidence anchor,
        Coordinates target, PixelPoint measured, int width, int height)
    {
        if (!anchor.IsFormal || !anchor.Result.Success || anchor.Result.Coordinates is null ||
            anchor.SourceWidthPixels != width || anchor.SourceHeightPixels != height)
            throw new ArgumentException("A same-detector formal orientation/scale anchor is required.");
        var inverse = G3WcsTargetProjector.SolveCenterForTargetAtPixel(target, anchor.Result,
            width, height, anchor.SolverIdentity, measured);
        return new PlateSolveResult
        {
            Success = true, Coordinates = inverse.DesiredG3Center,
            Pixscale = anchor.Result.Pixscale, PositionAngle = anchor.Result.PositionAngle,
            Flipped = anchor.Result.Flipped,
        };
    }

    internal static TargetIdentification TargetInMeasuredCoarseModel(TargetIdentification measured)
    {
        if (measured.Gate.Disposition != GateDisposition.Passed || measured.Target is null ||
            measured.Authority != TargetIdentificationAuthority.StellarCentroid ||
            string.IsNullOrWhiteSpace(measured.BoundShortPositionEvidencePath))
            throw new ArgumentException("An independently confirmed measured target is required.");
        // The local model's translation already fits this measured pixel. Its
        // catalogue projection is therefore this pixel, not the old mount hint.
        // Otherwise ProjectionDestination would apply the old discrepancy twice
        // (or reject the stellar authority with a catalogue-refinement receipt).
        // Keep the pair receipt/spread; model.Authority remains explicitly inferred.
        return measured with
        {
            PredictedPoint = measured.Target.Centroid,
            PredictionResidualPixels = 0,
            Authority = TargetIdentificationAuthority.CatalogWcsProjection,
            CatalogPositionRefinedFromSameFrame = true,
        };
    }

    private async Task<G3PlateSolveProbeState?> CreateMeasuredCoarseProbeAsync(ObservationContext context,
        G3WcsMotionPrediction prediction, G3PlateSolveProbeState longProbe, G3PlateSolveProbeState current,
        string firstPath, string firstSha, string confirmationPath, CancellationToken token)
    {
        if (!MeasuredCoarseAnchorUsable(prediction) || current.Image is null || current.MountBinding is null ||
            current.MeasuredPostWcsTarget?.Target is null || longProbe.MountBinding is null ||
            !TryGetCurrentG3SlitGeometryRunCache(context, out var cache) ||
            cache!.ImageWidthPixels != current.Image.Properties.Width || cache.ImageHeightPixels != current.Image.Properties.Height)
            return null;
        var anchor = prediction.FormalAnchor!;
        var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [anchor.SourcePath] = prediction.FormalAnchorSourceSha256!,
            [anchor.EvidencePath] = anchor.EvidenceSha256,
            [longProbe.FramePath] = longProbe.MountBinding.FrameSha256,
            [firstPath] = firstSha,
            [current.FramePath] = current.MountBinding.FrameSha256,
            [confirmationPath] = await ComputeFileSha256Async(confirmationPath, token).ConfigureAwait(false),
        };
        foreach (var source in sources)
            if (!SameHash(source.Value, await ComputeFileSha256Async(source.Key, token).ConfigureAwait(false)))
                throw new PhysicalActionGateException(GateResult.Unknown("G3_SPARSE_SOURCE_CHANGED",
                    "Measured local correction parent pixels/evidence changed; no new movement authorized."));
        var local = MeasuredCoarseLocalModel(anchor, TargetCoordinates(context.Plan),
            current.MeasuredPostWcsTarget.Target.Centroid, current.Image.Properties.Width, current.Image.Properties.Height);
        var uncertainty = current.MeasuredPostWcsTarget.CatalogPositionSpreadPixels + 1;
        var expires = new[] { prediction.SourceTiming!.CaptureCompletedUtc.AddSeconds(120),
            current.MountBinding.FrameCompletedUtc.AddSeconds(30) }.Min();
        var gate = GateResult.Warn(G3MeasuredCoarseRecoveryPolicy.ReadyCode,
            "独立短帧已确认目标；使用本轮邻场正式 WCS 的局部方向/尺度，按原账本尝试一次小幅粗修。不是本帧正式解算、精调放行或入缝成功。");
        var receipt = await PublishRunJsonEvidenceAsync("g3-measured-local-coarse-model",
            "Measured pixel offset with formal neighbour orientation/scale; one original-ledger correction only",
            new { gate, authority = G3MeasuredCoarseRecoveryPolicy.Authority, sources,
                measured = current.MeasuredPostWcsTarget, prediction.PredictedTargetPoint,
                formalAnchor = anchor.EvidencePath, local.Coordinates, local.Pixscale, local.PositionAngle,
                uncertaintyPixels = uncertainty, expires, maximumCorrections = 1,
                maximumPixels = G3MeasuredCoarseRecoveryPolicy.MaximumRadiusPixels,
                maximumArcseconds = G3MeasuredCoarseRecoveryPolicy.MaximumCorrectionArcseconds,
                budgetReset = false, freshDestinationImageRequired = true, formalSolve = false,
                slitArrivalOrScienceAuthorized = false }, current.FramePath, token).ConfigureAwait(false);
        var sha = await ComputeFileSha256Async(receipt, token).ConfigureAwait(false);
        sources[receipt] = sha;
        var inferred = new PlateSolveEvidence(local, TargetCoordinates(context.Plan), double.NaN,
            anchor.SolverIdentity, current.FramePath, receipt, sha, current.Image.Properties.Width,
            current.Image.Properties.Height, anchor.SourceBinning, DateTimeOffset.UtcNow)
        {
            Authority = G3MeasuredCoarseRecoveryPolicy.Authority, PredictionUncertaintyPixels = uncertainty,
            InferenceSources = sources, InferenceValidUntilUtc = expires,
            ExposureTiming = ProbeExposureTiming(current),
        };
        Report(gate.Message);
        return current with { Gate = gate, Solve = inferred, SummaryEvidencePath = receipt };
    }

    private async Task<G3FieldState> MeasuredCoarseFieldAsync(ObservationContext context,
        G3PlateSolveProbeState probe, CancellationToken token)
    {
        await RequireImmediatePhysicalActionGatesAsync(context, token).ConfigureAwait(false);
        var gate = await ValidateG3ProbeMountBindingForMotionAsync(context, probe, token).ConfigureAwait(false);
        if (gate.Disposition != GateDisposition.Passed)
            return G3FieldState.Failed(gate, probe.FramePath, probe.Image, probe.Solve, probe.MountBinding);
        if (!TryGetCurrentG3SlitGeometryRunCache(context, out var cache) || nightSetup is null)
            return G3FieldState.Failed(GateResult.Unknown("G3_MEASURED_COARSE_CONTEXT_CHANGED",
                "The run-scoped detector/slit context changed; local correction withheld."), probe.FramePath);
        var focus = c11RunFocusLock.Validate(ReadC11MainFocusOwner(), nightSetup.Value);
        if (focus.Disposition != GateDisposition.Passed) return G3FieldState.Failed(focus, probe.FramePath);
        var frame = G3FrameInputPolicy.Create(probe.Image!.Properties.Width, probe.Image.Properties.Height,
            probe.Image.Data.FlatArray, configuration.G3);
        return PreparedG3SlitGeometryField(cache!, probe.FramePath, probe.Image, frame, probe.MountBinding) with
        {
            Gate = probe.Gate, Solve = probe.Solve,
            TargetIdentification = TargetInMeasuredCoarseModel(probe.MeasuredPostWcsTarget!),
            MainFocusMeasurement = probe.ContentAssessment?.StellarMeasurement,
            ExposureTiming = ProbeExposureTiming(probe),
        };
    }
}
