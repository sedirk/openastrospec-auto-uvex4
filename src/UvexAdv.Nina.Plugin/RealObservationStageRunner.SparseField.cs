using System.IO;
using NINA.Astrometry;
using NINA.Core.Enum;
using NINA.Image.Interfaces;
using NINA.PlateSolving;
using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

internal sealed partial class RealObservationStageRunner
{
    private readonly List<SparseG3Node> sparseG3Nodes = new();
    private string? sparseG3Context;
    private readonly HashSet<string> attemptedSparseMosaics = new(StringComparer.Ordinal);
    private string? sparseSepWorkerSha;
    private string? sparseSepModuleSha;
    private string? sparseSepWorkerPath;
    private string? sparseSepModulePath;
    private const int MaximumSparseNodes = 16;

    private async Task<string[]> CurrentSearchOverlapSupportAsync(G3FieldState field, CancellationToken token)
    {
        var node = sparseG3Nodes.LastOrDefault(n => field.MountBinding is { } binding &&
            SameHash(n.Sha, binding.FrameSha256) && n.Path == field.FramePath);
        if (node is null || !node.StarsFromFrozenSep || node.Stars.Count < 5) return [];
        var component = sparseG3Nodes.Where(n => n.Component == node.Component).ToArray();
        // A shared component exists only after independent registration has
        // passed, not merely because five detections or a mount offset exist.
        var prior = component.LastOrDefault(n => n.Sha != node.Sha && n.StarsFromFrozenSep && n.Stars.Count >= 5);
        if (prior is null) return [];
        var window = G3SparseGraphWindowPolicy.Evaluate(node.Timing, prior.Timing,
            component.Select(n => n.Timing).ToArray(), DateTimeOffset.UtcNow,
            commissioning!.Value.Phd2SlitPlacement!.TargetSearchRadiusPixels);
        // Re-rooting would require a NEW direct registration; merely selecting
        // two young nodes cannot erase an older transform dependency here.
        if (!window.Usable || window.StartIndependentComponent) return [];
        foreach (var source in component) await ValidateSparseNodeHashesAsync(source, token).ConfigureAwait(false);
        // Revalidate the frozen extractor as well. Changed evidence must not
        // turn into permission for a longer blind search step.
        if (await TryGetSparseRegistrationStarsAsync(node.Sha, token).ConfigureAwait(false) is null) return [];
        return component.Select(n => n.Path).ToArray();
    }

    private async Task<IReadOnlyList<StarCandidate>?> TryGetSparseRegistrationStarsAsync(string sha, CancellationToken token)
    {
        var node = sparseG3Nodes.LastOrDefault(n => SameHash(n.Sha, sha));
        if (node is null || !node.StarsFromFrozenSep || sparseSepWorkerPath is null || sparseSepModulePath is null ||
            sparseSepWorkerSha is null || sparseSepModuleSha is null) return null;
        try
        {
            await ValidateSparseNodeHashesAsync(node, token).ConfigureAwait(false);
            if (SameHash(sparseSepWorkerSha, await ComputeFileSha256Async(sparseSepWorkerPath, token).ConfigureAwait(false)) &&
                SameHash(sparseSepModuleSha, await ComputeFileSha256Async(sparseSepModulePath, token).ConfigureAwait(false)))
                return node.Stars;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PhysicalActionGateException(GateResult.Unknown("G3_SPARSE_SOURCE_CHANGED",
                "A previously frozen source/extractor disappeared before reusing measured stars: " + ex.Message));
        }
        throw new PhysicalActionGateException(GateResult.Unknown("G3_SPARSE_SOURCE_CHANGED",
            "The frozen extractor changed before reusing this raw frame's measured field stars."));
    }

    // Image-only ingestion for already captured guide/lock frames. It does not
    // prove a stopped boundary, run a solver/model, capture again, or move a
    // device. A later acquisition consumer must still validate current owners,
    // stop/motion responsibility and fresh registered geometry before use.
    private async Task RetainSparseG3FrameAsync(ObservationContext context, IImageData image,
        string path, string sha, G3FieldMountBinding binding, G3FrameMountReadback beforeExposure,
        int exposureMilliseconds, PixelPoint? targetExclusion, CancellationToken token,
        G3SparseExposureTiming? captureTiming = null, bool captureTimingKnown = true)
    {
        await RequireImmediatePhysicalActionGatesAsync(context, token).ConfigureAwait(false);
        var owner = await phd2.ValidateIdentityAsync(PhdIdentityRequirement(), token).ConfigureAwait(false);
        if (!owner.IsValid) throw new PhysicalActionGateException(GateResult.Unknown("PHD2_IDENTITY_STALE",
            string.Join(" ", owner.Failures.Concat(owner.IndeterminateReasons))));
        var profile = ValidatePhdProfileBindingEvidence();
        if (profile.Disposition != GateDisposition.Passed) throw new PhysicalActionGateException(profile);
        var focus = c11RunFocusLock.Validate(ReadC11MainFocusOwner(), nightSetup!.Value);
        if (focus.Disposition != GateDisposition.Passed) throw new PhysicalActionGateException(focus);
        var integrity = G3FieldMountBindingPolicy.ValidateForMotion(binding, context.Plan.ObservationRunId,
            configuration.ActionConfigurationSha256, commissioning!.Sha256, path, sha,
            binding.RightAscensionDegrees, binding.DeclinationDegrees, binding.CoordinateEpoch,
            binding.PierSide, MountCommandArrivalToleranceArcseconds);
        if (integrity.Disposition != GateDisposition.Passed) throw new PhysicalActionGateException(integrity);
        if (!SameHash(sha, await ComputeFileSha256Async(path, token).ConfigureAwait(false)))
            throw new PhysicalActionGateException(GateResult.Unknown("G3_SPARSE_SOURCE_CHANGED",
                "The retained raw guide frame changed before image-only registration."));
        var content = await RunWithG3PendingReturnTimeAsync("retain-sparse-image",
            t => Task.Run(() => G3SolveProbeContentAnalyzer.Analyze(G3FrameInputPolicy.Create(
                image.Properties.Width, image.Properties.Height, image.Data.FlatArray, configuration.G3)), t), token).ConfigureAwait(false);
        if (content.Gate.Code == G3SolveProbeContentAnalyzer.OverexposedGateCode) return;
        var receipt = await PublishRunJsonEvidenceAsync("g3-sparse-retained-pixels", "Previously captured raw pixels; no motion/stop/solve authority",
            new { path, sha, exposureMilliseconds, beforeExposure.ReportedUtc, binding.FrameCompletedUtc,
                captureTimingKnown, captureTiming, timedGraphEligibleOnlyWithBoundedCapture = true,
                content.Gate, solverPerformed = false, stoppedBoundaryProved = false, budgetReset = false }, path, token).ConfigureAwait(false);
        var pending = new PlateSolveEvidence(new PlateSolveResult { Success = false }, TargetCoordinates(context.Plan),
            double.NaN, solverIdentity, path, receipt, await ComputeFileSha256Async(receipt, token).ConfigureAwait(false),
            image.Properties.Width, image.Properties.Height, configuration.G3.Binning, DateTimeOffset.UtcNow)
        { Authority = "CapturedPixelsOnly" };
        var probe = SparseProbe(image, path, binding, beforeExposure, exposureMilliseconds, content, pending);
        if (captureTimingKnown && captureTiming is not null)
        {
            // Only an independently owner-bounded exposure interval may
            // supplement the native bracket. The wait request alone does not
            // establish a shutter start; never rewrite its mount readback time.
            // Bad timing only withholds this optional image registration.
            if (!ValidRetainedSparseCaptureTiming(captureTiming, binding.FrameCompletedUtc,
                exposureMilliseconds, DateTimeOffset.UtcNow))
            {
                await WriteAuditBestEffortAsync("g3-sparse-retained-timing-unavailable", new {
                    path, captureTiming, binding.FrameCompletedUtc, exposureMilliseconds,
                    fallbackContinues = true, physicalStopProved = false }).ConfigureAwait(false);
                return;
            }
            probe = probe with { ExposureStartedUtc = captureTiming.CaptureRequestedUtc,
                ExposureEndedUtc = captureTiming.CaptureCompletedUtc,
                ExposureMillisecondsActual = exposureMilliseconds };
        }
        var retainedTiming = RetainedSparseCaptureTimingForGraph(new(probe.ExposureStartedUtc,
            binding.FrameCompletedUtc, exposureMilliseconds / 1000d, pending.CompletedUtc), captureTimingKnown);
        probe = probe with { ExposureStartedUtc = retainedTiming.CaptureRequestedUtc,
            ExposureEndedUtc = retainedTiming.CaptureCompletedUtc };
        await RegisterSparseG3NodeAsync(context, probe, sha, null, false, token, targetExclusion).ConfigureAwait(false);
        await RequireImmediatePhysicalActionGatesAsync(context, token).ConfigureAwait(false);
    }

    // Some rolling owners provide a new frame but no bounded shutter start.
    // Preserve its immutable pixels/SEP cache without fabricating a timestamp.
    // The existing timing policy will reject edges and inference from this node.
    internal static G3SparseExposureTiming RetainedSparseCaptureTimingForGraph(
        G3SparseExposureTiming timing, bool captureTimingKnown) =>
        captureTimingKnown ? timing : timing with { CaptureRequestedUtc = default };

    internal static bool ValidRetainedSparseCaptureTiming(G3SparseExposureTiming timing,
        DateTimeOffset actualCompletedUtc, int exposureMilliseconds, DateTimeOffset now) =>
        timing.CaptureRequestedUtc != default && actualCompletedUtc != default && exposureMilliseconds > 0 &&
        timing.CaptureCompletedUtc == actualCompletedUtc && timing.CaptureCompletedUtc <= now &&
        timing.CaptureCompletedUtc >= timing.CaptureRequestedUtc &&
        timing.SolveCompletedUtc >= timing.CaptureCompletedUtc && timing.SolveCompletedUtc <= now &&
        double.IsFinite(timing.ExposureSeconds) && timing.ExposureSeconds > 0 &&
        Math.Abs(timing.ExposureSeconds - exposureMilliseconds / 1000d) <= .001 &&
        timing.ExposureSeconds <= (timing.CaptureCompletedUtc - timing.CaptureRequestedUtc).TotalSeconds;

    private sealed record SparseG3Node(string Path, string Sha, IImageData Image, MonochromeFrame Frame,
        IReadOnlyList<StarCandidate> Stars, G3FieldMountBinding Binding, string Component,
        G3SimilarityTransform ReferenceToFrame, double PathUncertaintyPixels,
        PlateSolveEvidence? Anchor, PixelPoint? ExcludedTarget)
    {
        public G3SparseExposureTiming Timing { get; init; } = null!;
        public G3SparseDriftBound? Drift { get; init; }
        public bool StarsFromFrozenSep { get; init; }
    }

    private G3OverlapFootprint SparseOverlapFootprint(G3FieldState field) => new(
        field.Image?.Properties.Width ?? 0, field.Image?.Properties.Height ?? 0,
        field.Solve?.Result.Success == true ? field.Solve.Result.Pixscale :
            206.264806247 * configuration.G3.PixelSizeMicrometers * configuration.G3.Binning / configuration.G3.FocalLengthMillimeters,
        ImageXAxisEastToNorthDegrees: field.Solve is { Result.Success: true } solved
            ? G3WcsTargetProjector.ImageXAxisEastToNorthDegrees(solved.Result, solved.SolverIdentity) : null,
        EndpointPositionUncertaintyArcseconds: Math.Max(G3SearchStableEndpointToleranceArcseconds,
            (field.Solve?.PredictionUncertaintyPixels ?? 0) * (field.Solve?.Result.Pixscale ?? 0)),
        // A registered/mosaic WCS retains measured orientation. It is not an
        // unknown-axis field. Keep a wider angular error for inferred geometry.
        OrientationUncertaintyDegrees: field.Solve is { IsFormal: true, Result.Success: true } ? 1 : 5);

    private async Task ValidateSparseBoundaryAsync(ObservationContext context, G3PlateSolveProbeState probe,
        CancellationToken token)
    {
        await RequireImmediatePhysicalActionGatesAsync(context, token).ConfigureAwait(false);
        var owner = await phd2.ValidateIdentityAsync(PhdIdentityRequirement(), token).ConfigureAwait(false);
        if (!owner.IsValid) throw new PhysicalActionGateException(GateResult.Unknown("PHD2_IDENTITY_STALE",
            string.Join(" ", owner.Failures.Concat(owner.IndeterminateReasons))));
        var profile = ValidatePhdProfileBindingEvidence();
        if (profile.Disposition != GateDisposition.Passed) throw new PhysicalActionGateException(profile);
        var focus = c11RunFocusLock.Validate(ReadC11MainFocusOwner(), nightSetup!.Value);
        if (focus.Disposition != GateDisposition.Passed) throw new PhysicalActionGateException(focus);
        var binding = await ValidateG3ProbeMountBindingForMotionAsync(context, probe, token).ConfigureAwait(false);
        if (binding.Disposition != GateDisposition.Passed) throw new PhysicalActionGateException(binding);
        // The same return clock wraps CPU registration as well as each PL3 call.
        await RunWithG3PendingReturnTimeAsync("sparse-boundary", _ => Task.FromResult(true), token).ConfigureAwait(false);
    }

    private async Task<PlateSolveEvidence> ResolveSparseG3FrameAsync(ObservationContext context,
        IImageData image, string path, string sha, G3FieldMountBinding binding,
        G3FrameMountReadback beforeCapture, int exposureMilliseconds,
        G3SolveProbeContentAssessment content, PlateSolveEvidence direct,
        G3WcsMotionPrediction? prediction, bool allowRecovery, CancellationToken token)
    {
        var formalValid = direct.Result.Success && direct.Result.Coordinates is not null &&
            direct.Result.Flipped == configuration.G3.ExpectedWcsFlipped &&
            ValidateG3PlateSolvePlausibility(direct, image.Properties.Width, image.Properties.Height).Disposition == GateDisposition.Passed;
        var opticalMiss = allowRecovery && !direct.Result.Success &&
            content.Gate.Code != G3SolveProbeContentAnalyzer.OverexposedGateCode;
        var probe = SparseProbe(image, path, binding, beforeCapture, exposureMilliseconds, content, direct);
        direct = direct with { ExposureTiming = new(probe.ExposureStartedUtc, probe.ExposureEndedUtc,
            exposureMilliseconds / 1000d, direct.CompletedUtc) };
        probe = probe with { Solve = direct };
        SparseG3Node? current = null;
        var resolved = await G3SparseRecoveryWorkflow.ResolveAsync(direct, formalValid, opticalMiss,
            t => ValidateSparseBoundaryAsync(context, probe, t),
            async t =>
            {
                current = await RegisterSparseG3NodeAsync(context, probe, sha, prediction, formalValid, t).ConfigureAwait(false);
                if (formalValid) await CheckLocalDualModelAgainstFormalAsync(direct, t).ConfigureAwait(false);
            },
            t => current is null ? Task.FromResult<PlateSolveEvidence?>(null) : TryRegisteredSparseAnchorAsync(context, current, direct, t),
            t => current is null ? Task.FromResult<PlateSolveEvidence?>(null) : TryLocalDualSparseSolveAsync(context, probe, t),
            t => current is null ? Task.FromResult<PlateSolveEvidence?>(null) : TrySolveSparseMosaicAsync(context, current, direct, t), token).ConfigureAwait(false);
        return resolved.IsFormal && current is not null ? resolved with { MeasuredDrift = current.Drift } : resolved;
    }

    private static G3PlateSolveProbeState SparseProbe(IImageData image, string path, G3FieldMountBinding binding,
        G3FrameMountReadback beforeCapture, int exposureMilliseconds, G3SolveProbeContentAssessment content, PlateSolveEvidence direct)
    {
        var start = beforeCapture.ReportedUtc;
        var end = binding.FrameCompletedUtc;
        var headerStart = new DateTimeOffset(DateTime.SpecifyKind(image.MetaData.Image.ExposureStart, DateTimeKind.Utc));
        var headerEnd = headerStart.AddMilliseconds(exposureMilliseconds);
        // PHD2 save_image completion is not a shutter timestamp. Prefer DATE-OBS
        // only when it is consistent with this actual native capture bracket;
        // otherwise retain the wider PC bracket and its timing uncertainty.
        if (headerStart >= start && headerEnd <= end)
        { start = headerStart; end = headerEnd; }
        return new(content.Gate, path, image, direct, content,
            new[] { new G3PlateSolveAttemptEvidence(0, exposureMilliseconds, "G3_SPARSE_CURRENT_FRAME",
                GateDisposition.Passed, direct.IsFormal && direct.Result.Success, path, direct.EvidencePath,
                MountBinding: binding) }, MountBinding: binding, BeforeExposureMountReadback: beforeCapture)
        { ExposureMillisecondsActual = exposureMilliseconds, ExposureStartedUtc = start, ExposureEndedUtc = end };
    }

    private async Task<PlateSolveEvidence?> TrySparseRegisteredBeforeSolveAsync(ObservationContext context,
        IImageData image, string path, string sha, G3FieldMountBinding binding,
        G3FrameMountReadback beforeCapture, int exposureMilliseconds, G3SolveProbeContentAssessment content,
        Coordinates hint, G3WcsMotionPrediction? prediction, CancellationToken token)
    {
        var anchor = sparseG3Nodes.LastOrDefault(n => n.Anchor is not null)?.Anchor;
        // A warm, independently qualified local model is also an optical
        // anchor. Check it BEFORE spending a failed G3 solve's latency. This
        // existence check grants nothing; the archive/holdout/focus gates below
        // decide whether any QHY witness may actually be requested.
        var possibleLocalModel = configuration.G3.EffectiveFastSolvePair.Enabled &&
            Directory.Exists(LocalDualArchiveRoot) &&
            Directory.EnumerateFiles(LocalDualArchiveRoot, "local-candidate-*.json", SearchOption.AllDirectories).Any();
        if (sparseG3Nodes.Count == 0 && !possibleLocalModel) return null;
        var receipt = await PublishRunJsonEvidenceAsync("g3-sparse-before-solve", "Current image registration before another full solve",
            new { currentFrame = path, currentSha256 = sha, exposureMilliseconds,
                captureRequestedUtc = beforeCapture.ReportedUtc, binding.FrameCompletedUtc,
                nativeCurrentSolveAttempted = false, originalBudgetRetained = true }, path, token).ConfigureAwait(false);
        var notAttempted = new PlateSolveEvidence(new PlateSolveResult { Success = false }, hint, double.NaN,
            anchor?.SolverIdentity ?? solverIdentity, path, receipt, await ComputeFileSha256Async(receipt, token).ConfigureAwait(false),
            image.Properties.Width, image.Properties.Height, configuration.G3.Binning, DateTimeOffset.UtcNow);
        var probe = SparseProbe(image, path, binding, beforeCapture, exposureMilliseconds, content, notAttempted);
        SparseG3Node? current = null;
        return await G3SparseRecoveryWorkflow.TryAvailableBeforeSolveAsync(
            t => ValidateSparseBoundaryAsync(context, probe, t),
            async t => current = await RegisterSparseG3NodeAsync(context, probe, sha, prediction, false, t).ConfigureAwait(false),
            t => current is null ? Task.FromResult<PlateSolveEvidence?>(null) : TryRegisteredSparseAnchorAsync(context, current, notAttempted, t),
            t => current is null || !possibleLocalModel ? Task.FromResult<PlateSolveEvidence?>(null) : TryLocalDualSparseSolveAsync(context, probe, t),
            t => current is null ? Task.FromResult<PlateSolveEvidence?>(null) : TrySolveSparseMosaicAsync(context, current, notAttempted, t),
            token).ConfigureAwait(false);
    }

    private async Task<SparseG3Node?> RegisterSparseG3NodeAsync(ObservationContext context,
        G3PlateSolveProbeState probe, string sha, G3WcsMotionPrediction? prediction, bool formal,
        CancellationToken token, PixelPoint? retainedTargetExclusion = null)
    {
        var image = probe.Image!; var binding = probe.MountBinding!;
        var geometry = $"{context.Plan.ObservationRunId}|{configuration.ActionConfigurationSha256}|{commissioning!.Sha256}|{nightSetup!.Sha256}|{phd2.Snapshot.ConnectionEpoch}|{binding.CoordinateEpoch}|{binding.PierSide}|{c11RunFocusLock.Position}|{image.Properties.Width}|{image.Properties.Height}";
        if (sparseG3Context != geometry)
        {
            if (sparseG3Nodes.Count > 0)
                await WriteAuditBestEffortAsync("g3-overlap-graph-retired", new { nodes = sparseG3Nodes.Count,
                    reason = "context changed",
                    budgetReset = false }).ConfigureAwait(false);
            sparseG3Nodes.Clear();
            sparseG3Context = geometry;
        }
        var duplicateIndex = sparseG3Nodes.FindIndex(n => n.Sha.Equals(sha, StringComparison.OrdinalIgnoreCase));
        if (duplicateIndex >= 0)
        {
            var duplicate = sparseG3Nodes[duplicateIndex];
            var sameCapture = string.Equals(Path.GetFullPath(duplicate.Path), Path.GetFullPath(probe.FramePath), StringComparison.OrdinalIgnoreCase) &&
                SameHash(duplicate.Binding.BindingSha256,binding.BindingSha256) && SameHash(binding.FrameSha256,sha);
            if (sameCapture)
            {
                // Pre-solver registration and post-solver promotion of this SAME
                // capture do not create another independent node or spend new budget.
                await ValidateSparseNodeHashesAsync(duplicate,token).ConfigureAwait(false);
                var upgraded = duplicate with { Anchor = formal ? probe.Solve : duplicate.Anchor,
                    Timing = duplicate.Timing with { SolveCompletedUtc = probe.Solve!.CompletedUtc },
                    ExcludedTarget = formal ? G3WcsTargetProjector.Project(TargetCoordinates(context.Plan),probe.Solve!.Result,
                        duplicate.Frame.Width,duplicate.Frame.Height,probe.Solve.SolverIdentity) : duplicate.ExcludedTarget };
                if (formal) upgraded = IndependentSparseRoot(upgraded) with { Drift = duplicate.Drift };
                sparseG3Nodes[duplicateIndex] = upgraded;
                return upgraded;
            }
            await WriteAuditBestEffortAsync("g3-overlap-duplicate-not-adopted", new { probe.FramePath,
                duplicate.Path, sha, reason = "Repeated bytes cannot establish an independent fresh inference source." }).ConfigureAwait(false);
            return null;
        }
        if (sparseG3Nodes.Count >= MaximumSparseNodes)
        {
            var recent = sparseG3Nodes[^1];
            await ValidateSparseNodeHashesAsync(recent, token).ConfigureAwait(false);
            await WriteAuditBestEffortAsync("g3-overlap-graph-retired",new {nodes=sparseG3Nodes.Count,
                reason="bounded graph capacity reached; keep the newest actual raw frame as an independent root",
                retainedFrame=recent.Path,budgetReset=false}).ConfigureAwait(false);
            sparseG3Nodes.Clear();
            sparseG3Nodes.Add(IndependentSparseRoot(recent));
        }
        var frame = G3FrameInputPolicy.Create(image.Properties.Width, image.Properties.Height, image.Data.FlatArray, configuration.G3);
        var extraction = await ExtractSparseRegistrationStarsAsync(probe, frame, token).ConfigureAwait(false);
        var stars = extraction.Stars;
        var target = formal ? G3WcsTargetProjector.Project(TargetCoordinates(context.Plan), probe.Solve!.Result,
            frame.Width, frame.Height, probe.Solve.SolverIdentity) : prediction?.PredictedTargetPoint ?? retainedTargetExclusion;
        var node = new SparseG3Node(probe.FramePath, sha, image, frame, stars, binding, sha,
            G3SimilarityTransform.Identity, 0, formal ? probe.Solve : null, target)
        { Timing = new(probe.ExposureStartedUtc, probe.ExposureEndedUtc,
            probe.ExposureMillisecondsActual / 1000d, probe.Solve!.CompletedUtc), StarsFromFrozenSep = extraction.FrozenSep };
        // A native current-frame formal solution is independent of every old
        // registration branch. Do not bind its future use to old graph timing,
        // transforms or inferred uncertainty merely because the pixels overlap.
        // Recent direct pixel pairs may still measure drift for this native
        // solve. They never make the independent formal root inherit an old
        // graph transform, uncertainty or timing dependency.
        SparseG3Node? joined = null;
        var graphConflict = false;
        foreach (var prior in sparseG3Nodes.AsEnumerable().Reverse().ToArray())
        {
            if (joined is not null && prior.Component != joined.Component) continue;
            var window = G3SparseGraphWindowPolicy.Evaluate(node.Timing, prior.Timing,
                sparseG3Nodes.Where(n => n.Component == prior.Component).Select(n => n.Timing).ToArray(),
                DateTimeOffset.UtcNow, commissioning!.Value.Phd2SlitPlacement!.TargetSearchRadiusPixels);
            if (!window.Usable)
            {
                await WriteAuditBestEffortAsync("g3-overlap-time-unavailable", new { prior.Path, current = node.Path, window.Timing }).ConfigureAwait(false);
                continue;
            }
            if (window.StartIndependentComponent && joined is not null) continue;
            await ValidateSparseNodeHashesAsync(prior, token).ConfigureAwait(false);
            var registration = await RunWithG3PendingReturnTimeAsync("image-registration",
                t => Task.Run(() => G3OverlapRegistration.Register(prior.Stars, stars, frame.Width, frame.Height,
                    SparseExclusions(prior.ExcludedTarget), SparseExclusions(target), t), t), token).ConfigureAwait(false);
            var registrationReceipt = await PublishRunJsonEvidenceAsync("g3-overlap-registration", "Independent neighbouring-image registration",
                new { reference = prior.Path, referenceSha256 = prior.Sha, current = node.Path, currentSha256 = sha,
                    registration, targetExcluded = target is not null, currentMountBinding = binding,
                    graphPolicy = G3SparseRecoveryWorkflow.Version, motionIssued = false, budgetReset = false },
                node.Path, token).ConfigureAwait(false);
            if (!registration.Accepted || registration.Transform is null) continue;
            var reference = prior;
            if (window.StartIndependentComponent && !formal)
            {
                // This DIRECT pair was just measured from the two immutable
                // raw images. Its new pixel origin does not depend on the old
                // component. Only a native formal anchor may survive rebasing;
                // an inferred old anchor retains its parents and is not washed
                // into a young component or a new expiry.
                reference = IndependentSparseRoot(prior);
                var rootIndex = sparseG3Nodes.IndexOf(prior);
                sparseG3Nodes[rootIndex] = reference;
                await WriteAuditBestEffortAsync("g3-overlap-recent-component-root", new
                {
                    reference.Path, current = node.Path, discardedInferredAnchor = prior.Anchor is { IsFormal: false },
                    nativeAnchorRetained = reference.Anchor is not null, window.Timing, budgetReset = false,
                }).ConfigureAwait(false);
            }
            G3SparseDriftBound? drift = null;
            var elapsedMinimum = (node.Timing.EarliestMidpointUtc - prior.Timing.LatestMidpointUtc).TotalSeconds;
            var mountSpan = G3AcquisitionMotionPlanner.AngularSeparationArcseconds(prior.Binding.RightAscensionDegrees,
                prior.Binding.DeclinationDegrees, node.Binding.RightAscensionDegrees, node.Binding.DeclinationDegrees);
            if (elapsedMinimum > 0 && mountSpan <= MountCommandArrivalToleranceArcseconds)
            {
                var measuredMotion = G3OverlapGraphConsistency.Evaluate(G3SimilarityTransform.Identity,
                    registration.Transform, frame.Width, frame.Height).MaximumCornerDisagreementPixels;
                // Independent image registration already measures ALL optical
                // displacement, including any tracking/readback error. Mount
                // tolerance qualifies stationarity; adding it again as pixel
                // motion fabricated ~10 px per pair and expired useful mosaics.
                drift = new(G3SparseFrameTimingPolicy.RegisteredDriftUpperBound(
                    measuredMotion, registration.MaximumHoldoutResidualPixels, elapsedMinimum), registrationReceipt);
            }
            if (formal)
            {
                if (drift is not null) { node = node with { Drift = drift }; break; }
                continue;
            }
            var proposal = node with { Component = reference.Component,
                ReferenceToFrame = reference.ReferenceToFrame.Then(registration.Transform),
                PathUncertaintyPixels = reference.PathUncertaintyPixels + Math.Max(1, registration.MaximumHoldoutResidualPixels),
                Drift = drift };
            if (joined is null) joined = proposal;
            else
            {
                var closure = G3OverlapGraphConsistency.Evaluate(joined.ReferenceToFrame,proposal.ReferenceToFrame,frame.Width,frame.Height);
                await PublishRunJsonEvidenceAsync("g3-overlap-loop-closure","Independent registration paths must agree",
                    new {current=node.Path,currentSha256=sha,reference=prior.Path,referenceSha256=prior.Sha,
                        component=joined.Component,firstTransform=joined.ReferenceToFrame,alternateTransform=proposal.ReferenceToFrame,
                        closure,budgetReset=false,motionIssued=false},node.Path,token).ConfigureAwait(false);
                if (!closure.Consistent) { graphConflict=true;break; }
                if (proposal.Drift is { } observed && (joined.Drift is null || observed.UpperPixelsPerSecond > joined.Drift.UpperPixelsPerSecond))
                    joined = joined with { Drift = observed };
            }
        }
        if (!graphConflict && joined is not null) node=joined;
        if (graphConflict)
            await WriteAuditBestEffortAsync("g3-overlap-graph-inconsistent",new {node.Path,sha,
                reason="Measured same-component paths disagree; retain as an isolated node, not inferred WCS.",
                formalSolveRetained=formal,budgetReset=false}).ConfigureAwait(false);
        sparseG3Nodes.Add(node);
        return node;
    }

    private static SparseG3Node IndependentSparseRoot(SparseG3Node node) => node with
    {
        Component = node.Sha, ReferenceToFrame = G3SimilarityTransform.Identity,
        PathUncertaintyPixels = 0, Drift = null,
        Anchor = node.Anchor is { IsFormal: true } ? node.Anchor : null,
    };

    private sealed record SparseG3StarExtraction(IReadOnlyList<StarCandidate> Stars, bool FrozenSep);

    private async Task<SparseG3StarExtraction> ExtractSparseRegistrationStarsAsync(
        G3PlateSolveProbeState probe, MonochromeFrame frame, CancellationToken token)
    {
        SepDetectionRuntime runtime;
        string workerSha, moduleSha, module;
        try
        {
            runtime = SepStarDetectionClient.ReadRuntime(Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData), "UVEX-ADV", "star-detection", "runtime.json"));
            module = Path.Combine(Path.GetDirectoryName(runtime.WorkerPath)!, "sep_detector.py");
            workerSha = await ComputeFileSha256Async(runtime.WorkerPath, token).ConfigureAwait(false);
            moduleSha = await ComputeFileSha256Async(module, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or ArgumentException or InvalidOperationException)
        {
            await WriteAuditBestEffortAsync("g3-overlap-extraction-fallback", new { reason = ex.Message,
                extraction = "existing stellar analysis; SEP runtime unavailable" }).ConfigureAwait(false);
            return new(probe.ContentAssessment!.StellarMeasurement.Stars, false);
        }
        if ((sparseSepWorkerSha is not null && !SameHash(sparseSepWorkerSha, workerSha)) ||
            (sparseSepModuleSha is not null && !SameHash(sparseSepModuleSha, moduleSha)))
            throw new PhysicalActionGateException(GateResult.Unknown("G3_SPARSE_SOURCE_CHANGED", "The frozen image extractor changed within this runner."));
        sparseSepWorkerSha = workerSha; sparseSepModuleSha = moduleSha;
        sparseSepWorkerPath = runtime.WorkerPath; sparseSepModulePath = module;
        SepImageMeasurements measurements;
        try
        {
            measurements = await RunWithG3PendingReturnTimeAsync("overlap-source-extraction",
                t => new SepStarDetectionClient().DetectAsync(runtime, frame.Width, frame.Height,
                    probe.Image!.Data.FlatArray, frame.SaturationLevel, t), token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or
            System.ComponentModel.Win32Exception or InvalidOperationException ||
            (ex is OperationCanceledException && !token.IsCancellationRequested))
        {
            // Optional image-only extraction may be unavailable; never turn a
            // successful formal solve into a whole-night process failure.
            // Return-reserve and physical/ownership exceptions are not caught.
            await WriteAuditBestEffortAsync("g3-overlap-extraction-fallback", new { reason = ex.Message,
                extraction = "existing stellar analysis; optional SEP extraction unavailable" }).ConfigureAwait(false);
            return new(probe.ContentAssessment!.StellarMeasurement.Stars, false);
        }
        if (!SameHash(workerSha, await ComputeFileSha256Async(runtime.WorkerPath, token).ConfigureAwait(false)) ||
            !SameHash(moduleSha, await ComputeFileSha256Async(module, token).ConfigureAwait(false)))
            throw new PhysicalActionGateException(GateResult.Unknown("G3_SPARSE_SOURCE_CHANGED", "The image extractor changed during registration measurement."));
        var stars = CatalogFieldRegistration.ReferenceStars(measurements);
        await PublishRunJsonEvidenceAsync("g3-overlap-source-extraction", "Independent SEP field stars for image registration",
            new { probe.FramePath, workerSha, moduleSha, stars, targetIdentityGranted = false }, probe.FramePath, token).ConfigureAwait(false);
        return new(stars, true);
    }

    private G3RegistrationExclusion[]? SparseExclusions(PixelPoint? target) => target is null ? null :
        new[] { new G3RegistrationExclusion(target, Math.Max(30, commissioning!.Value.Phd2SlitPlacement!.TargetSearchRadiusPixels)) };

    private async Task ValidateSparseNodeHashesAsync(SparseG3Node node, CancellationToken token)
    {
        if (!SameHash(node.Sha, await ComputeFileSha256Async(node.Path, token).ConfigureAwait(false)) ||
            (node.Anchor is { } anchor && !SameHash(anchor.EvidenceSha256,
                await ComputeFileSha256Async(anchor.EvidencePath, token).ConfigureAwait(false))))
            throw new PhysicalActionGateException(GateResult.Unknown("G3_SPARSE_SOURCE_CHANGED",
                "An immutable sparse-field source changed; inference and new motion are withheld."));
    }

    private async Task<PlateSolveEvidence?> TryRegisteredSparseAnchorAsync(ObservationContext context,
        SparseG3Node current, PlateSolveEvidence direct, CancellationToken token)
    {
        var anchor = sparseG3Nodes.LastOrDefault(n => n.Component == current.Component && n.Anchor is not null && n.Sha != current.Sha);
        if (anchor?.Anchor is not { } solve) return null;
        foreach (var node in sparseG3Nodes.Where(n => n.Component == current.Component))
            await ValidateSparseNodeHashesAsync(node, token).ConfigureAwait(false);
        var transform = anchor.ReferenceToFrame.Inverse().Then(current.ReferenceToFrame);
        PlateSolveResult result;
        try { result = G3RegisteredWcs.RestoreCurrentResult(solve.Result, anchor.Frame.Width, anchor.Frame.Height,
            solve.SolverIdentity, transform, current.Frame.Width, current.Frame.Height); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            await WriteAuditBestEffortAsync("g3-registered-wcs-rejected", new { reason = ex.Message, current.Path }).ConfigureAwait(false);
            return null;
        }
        return await CreateSparseInferenceAsync(context, current, direct, result, solve.SolverIdentity,
            "RegisteredNeighbourWcs", RegisteredSparseUncertainty(solve.PredictionUncertaintyPixels,
                transform.Scale, anchor.PathUncertaintyPixels, current.PathUncertaintyPixels),
            new { anchor = anchor.Path, sourceSolve = solve.EvidencePath, transform,
                parents = sparseG3Nodes.Where(n => n.Component == current.Component).Select(n => new { n.Path, n.Sha, n.ReferenceToFrame }) }, token).ConfigureAwait(false);
    }

    private async Task<PlateSolveEvidence?> TrySolveSparseMosaicAsync(ObservationContext context,
        SparseG3Node current, PlateSolveEvidence direct, CancellationToken token)
    {
        var nodes = sparseG3Nodes.Where(n => n.Component == current.Component).ToArray();
        // Reaching this port means the usable registered/local result was not
        // obtained. The mere existence of an unusable anchor must not suppress
        // an independent formal solve of the available real pixel mosaic.
        // Once for each actual connected SOURCE SET, regardless of enumeration
        // order or whether its unusable optical anchor was retained.
        if (!G3SparseRecoveryWorkflow.TryBeginMosaic(nodes.Select(n => n.Sha).ToArray(), attemptedSparseMosaics)) return null;
        foreach (var node in nodes) await ValidateSparseNodeHashesAsync(node, token).ConfigureAwait(false);
        G3MosaicComposition composition;
        try
        {
            composition = await RunWithG3PendingReturnTimeAsync("mosaic-compose", t => Task.Run(() =>
                G3MosaicComposer.Compose(nodes.Select(n => new G3MosaicInput(n.Frame, n.ReferenceToFrame,
                    n.Path, n.Sha, Math.Max(.001, n.Image.MetaData.Image.ExposureTime))).ToArray(), t), t), token).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            await WriteAuditBestEffortAsync("g3-mosaic-unavailable", new { reason = ex.Message }).ConfigureAwait(false);
            return null;
        }
        var mosaic = G3MosaicImage.Create(current.Image, imageDataFactory, composition);
        var reserved = ReserveRunEvidencePath("g3-derived-overlap-mosaic", ".fits");
        var save = new NINA.Image.FileFormat.FileSaveInfo(profileService)
        {
            FilePath = Path.GetDirectoryName(reserved)!, FilePattern = Path.GetFileNameWithoutExtension(reserved),
            FileType = FileTypeEnum.FITS, ForceExtension = ".fits", FITSUseLegacyWriter = false,
        };
        var path = await RunWithG3PendingReturnTimeAsync("save-derived-mosaic",
            t => mosaic.SaveToDisk(save, t, true), token).ConfigureAwait(false);
        var manifest = await PublishRunJsonEvidenceAsync("g3-derived-mosaic-parents", "Derived pixel mosaic, not a raw observation",
            new { path, composition.Width, composition.Height, composition.ReferencePixelOffset,
                composition.CoveredFraction, composition.SourceContributions, composition.SourceSha256,
                parents = nodes.Select(n => new { n.Path, n.Sha, n.ReferenceToFrame, n.PathUncertaintyPixels }),
                targetFrame = current.Path, formalSolvePerformed = false, budgetReset = false }, path, token).ConfigureAwait(false);
        PublishEvidencePathOnce("g3-derived-overlap-mosaic-fits", path,
            new Dictionary<string, string> { ["derived"] = "true", ["parents"] = manifest },
            await ComputeFileSha256Async(path, token).ConfigureAwait(false));
        var solved = await SolveImageAsync(mosaic, configuration.G3.FocalLengthMillimeters,
            configuration.G3.PixelSizeMicrometers, configuration.G3.Binning, direct.Requested,
            "PHD2/G3 derived registered mosaic", path, token).ConfigureAwait(false);
        foreach (var node in nodes) await ValidateSparseNodeHashesAsync(node, token).ConfigureAwait(false);
        if (!solved.Result.Success || solved.Result.Coordinates is null) return null;
        if (solved.Result.Flipped != configuration.G3.ExpectedWcsFlipped ||
            ValidateG3PlateSolvePlausibility(solved, composition.Width, composition.Height).Disposition != GateDisposition.Passed)
            return null;
        PlateSolveResult currentResult;
        try { currentResult = G3RegisteredWcs.RestoreFromMosaic(solved.Result, composition, solved.SolverIdentity,
            current.ReferenceToFrame, current.Frame.Width, current.Frame.Height); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            await WriteAuditBestEffortAsync("g3-mosaic-projection-rejected", new { reason = ex.Message }).ConfigureAwait(false);
            return null;
        }
        var inferred = await CreateSparseInferenceAsync(context, current, direct, currentResult, solved.SolverIdentity,
            "RegisteredMosaicWcs", 3 + nodes.Max(n => n.PathUncertaintyPixels) + current.PathUncertaintyPixels,
            new { mosaicFrame = path, mosaicEvidence = manifest, formalMosaicSolve = solved.EvidencePath,
                sourceSolveSha256 = solved.EvidenceSha256, current.ReferenceToFrame }, token,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [path] = await ComputeFileSha256Async(path, token).ConfigureAwait(false),
                [manifest] = await ComputeFileSha256Async(manifest, token).ConfigureAwait(false),
                [solved.EvidencePath] = solved.EvidenceSha256
            }).ConfigureAwait(false);
        if (inferred is not null)
        {
            // Retain a solved MOSAIC anchor for later registered images, but do
            // not relabel it a native solve or allow it into dual-model training.
            var index = sparseG3Nodes.IndexOf(current);
            if (index >= 0) sparseG3Nodes[index] = current with { Anchor = inferred };
        }
        return inferred;
    }

    internal static double RegisteredSparseUncertainty(double anchorUncertainty, double scale,
        double anchorPathUncertainty, double currentPathUncertainty) =>
        Math.Max(3, anchorUncertainty * scale) + anchorPathUncertainty + currentPathUncertainty;

    private async Task<PlateSolveEvidence?> CreateSparseInferenceAsync(ObservationContext context, SparseG3Node current,
        PlateSolveEvidence direct, PlateSolveResult result, string projectionIdentity, string authority,
        double uncertaintyPixels, object provenance, CancellationToken token,
        IReadOnlyDictionary<string, string>? additionalSources = null)
    {
        var maximum = commissioning!.Value.Phd2SlitPlacement!.TargetSearchRadiusPixels;
        if (!double.IsFinite(uncertaintyPixels) || uncertaintyPixels <= 0 || uncertaintyPixels > maximum) return null;
        var temporal = G3SparseFrameTimingPolicy.Evaluate(current.Timing, DateTimeOffset.UtcNow,
            G3SparseTimingPurpose.RegisteredCurrentFrame, uncertaintyPixels, maximum,
            sparseG3Nodes.Where(n => n.Component == current.Component).Select(n => n.Timing).ToArray(), current.Drift);
        if (!temporal.Usable)
        {
            await WriteAuditBestEffortAsync("g3-sparse-temporal-inference-rejected", new { authority, current.Path, temporal }).ConfigureAwait(false);
            return null;
        }
        uncertaintyPixels = temporal.UncertaintyPixels;
        var validUntil = current.Timing.EarliestMidpointUtc + G3SparseFrameTimingLimits.Default.MaximumCurrentMidpointAge;
        if (current.Drift is { UpperPixelsPerSecond: > 0 } measuredDrift)
        {
            var uncertaintyExpiry = DateTimeOffset.UtcNow.AddSeconds(Math.Max(0, maximum - uncertaintyPixels) / measuredDrift.UpperPixelsPerSecond);
            if (uncertaintyExpiry < validUntil) validUntil = uncertaintyExpiry;
        }
        var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        { [current.Path] = current.Sha, [direct.EvidencePath] = direct.EvidenceSha256 };
        foreach (var node in sparseG3Nodes.Where(n => n.Component == current.Component))
        {
            sources[node.Path] = node.Sha;
            if (node.Anchor is { } anchor)
            {
                sources[anchor.EvidencePath] = anchor.EvidenceSha256;
                foreach (var parent in anchor.InferenceSources) sources[parent.Key] = parent.Value;
            }
        }
        if (additionalSources is not null)
            foreach (var source in additionalSources) sources[source.Key] = source.Value;
        var receipt = await PublishRunJsonEvidenceAsync("g3-sparse-inferred-wcs", "Inferred current field; NOT a successful native current-frame solve",
            new { authority, policyVersion = G3SparseRecoveryWorkflow.Version, currentFrame = current.Path,
                currentSha256 = current.Sha, directSolveEvidence = direct.EvidencePath, nativeCurrentSolveSucceeded = false,
                inferredCentreRaDegrees = result.Coordinates.RADegrees, inferredCentreDecDegrees = result.Coordinates.Dec,
                result.Pixscale, result.PositionAngle, result.Flipped, uncertaintyPixels, provenance, sources,
                temporal, current.Timing, current.Drift, validUntilUtc = validUntil,
                current.Binding, independentShortTargetConfirmationRequired = true, budgetReset = false,
                fineMotionOrScienceAuthorized = false }, current.Path, token).ConfigureAwait(false);
        sources[receipt] = await ComputeFileSha256Async(receipt, token).ConfigureAwait(false);
        Report($"稀疏场退化已取得 {authority}，预测不确定度 {uncertaintyPixels:F1}px；不是本帧正式解，后续须独立短帧确认目标，运动仍用原账本。");
        var completedUtc = DateTimeOffset.UtcNow;
        return new PlateSolveEvidence(result, direct.Requested, AngularSeparationArcseconds(direct.Requested, result.Coordinates),
            projectionIdentity, current.Path, receipt, await ComputeFileSha256Async(receipt, token).ConfigureAwait(false),
            current.Frame.Width, current.Frame.Height, configuration.G3.Binning, completedUtc)
        { Authority = authority, PredictionUncertaintyPixels = uncertaintyPixels, InferenceSources = sources,
            InferenceValidUntilUtc = validUntil, ExposureTiming = current.Timing with { SolveCompletedUtc = completedUtc },
            MeasuredDrift = current.Drift };
    }

    internal static async Task<GateResult> ValidateSparseInferenceSourcesAsync(PlateSolveEvidence solve,
        Func<string, CancellationToken, Task<string>> hashFile, CancellationToken token)
    {
        if (solve.IsFormal) return GateResult.Pass("G3_FORMAL_SOURCE", "No inferred source chain.");
        if (solve.InferenceValidUntilUtc is null || DateTimeOffset.UtcNow > solve.InferenceValidUntilUtc)
            return GateResult.TargetQuality("G3_CATALOG_SHORT_POSITION_UNCONFIRMED",
                "The inferred image position expired during exposure/processing latency; it must not authorize a new correction without a fresh optical witness.");
        if (solve.InferenceSources.Count < 2 || !solve.InferenceSources.ContainsKey(solve.SourcePath) ||
            !solve.InferenceSources.ContainsKey(solve.EvidencePath))
            return GateResult.Unknown("G3_SPARSE_SOURCE_CHANGED", "The inferred field has no complete immutable source chain; no new motion is authorized.");
        foreach (var source in solve.InferenceSources)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (SameHash(source.Value, await hashFile(source.Key, token).ConfigureAwait(false))) continue;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return GateResult.Unknown("G3_SPARSE_SOURCE_CHANGED", "An inferred field's parent evidence changed or is unavailable; stop before using its coordinates.");
        }
        token.ThrowIfCancellationRequested();
        if (DateTimeOffset.UtcNow > solve.InferenceValidUntilUtc)
            return GateResult.TargetQuality("G3_CATALOG_SHORT_POSITION_UNCONFIRMED",
                "The optical position expired while rechecking its immutable source chain; do not issue a new correction from it.");
        return GateResult.Pass("G3_SPARSE_SOURCES_UNCHANGED", "All inferred current-field sources remain hash-bound.");
    }

    internal static async Task<GateResult> ValidateMeasuredSparseContinuityAsync(G3FieldState field,
        Func<string, CancellationToken, Task<string>> hashFile, CancellationToken token)
    {
        var solve = field.Solve!;
        if (field.SparseMeasuredContinuity is not { } proof)
            return await ValidateSparseInferenceSourcesAsync(solve, hashFile, token).ConfigureAwait(false);
        var track = field.TargetTrackSource;
        if (!field.SparseInferenceConfirmed || field.Gate.Disposition != GateDisposition.Passed ||
            !field.TargetIdentification.HasCatalogPositionRefinement || track is null ||
            solve.InferenceValidUntilUtc is not { } expiry || proof.AssociationValidatedUtc > expiry ||
            proof.AssociationValidatedUtc < track.Timing.CaptureCompletedUtc ||
            proof.PositionValidUntilUtc > track.Timing.CaptureCompletedUtc.AddSeconds(30) ||
            proof.CurrentFramePath != track.Path || !SameHash(proof.CurrentFrameSha256, track.Sha) ||
            track.Target != field.TargetIdentification.Target?.Centroid ||
            !SameHash(proof.SolveEvidenceSha256, solve.EvidenceSha256) ||
            proof.ConfirmationPath != field.TargetIdentification.BoundShortPositionEvidencePath ||
            proof.FirstFramePath == proof.CurrentFramePath || SameHash(proof.FirstFrameSha256, proof.CurrentFrameSha256))
            return GateResult.Unknown("G3_SPARSE_SOURCE_CHANGED", "Measured continuity lacks its independently confirmed, timely catalogue association.");
        var now = DateTimeOffset.UtcNow;
        var temporal = G3SparseFrameTimingPolicy.Evaluate(track.Timing, now,
            G3SparseTimingPurpose.RegisteredCurrentFrame, field.TargetIdentification.CatalogPositionSpreadPixels,
            proof.MaximumPositionUncertaintyPixels, measuredDrift: solve.MeasuredDrift);
        if (!temporal.Usable || now > proof.PositionValidUntilUtc)
            return GateResult.TargetQuality("G3_CATALOG_SHORT_POSITION_UNCONFIRMED", "The measured short-frame position is stale; no motion from old pixels.");
        // All original parents are STILL hashed, as are both short frames and
        // their accepted receipt. Only positional freshness changes authority;
        // neither the old WCS nor its recorded expiry is rewritten.
        var parents = new Dictionary<string, string>(solve.InferenceSources, StringComparer.OrdinalIgnoreCase)
        {
            [proof.ConfirmationPath] = proof.ConfirmationSha256,
            [proof.FirstFramePath] = proof.FirstFrameSha256,
            [proof.CurrentFramePath] = proof.CurrentFrameSha256,
        };
        var integrity = await ValidateSparseInferenceSourcesAsync(solve with
        {
            InferenceSources = parents,
            InferenceValidUntilUtc = proof.PositionValidUntilUtc,
        }, hashFile, token).ConfigureAwait(false);
        if (integrity.Disposition != GateDisposition.Passed) return integrity;
        // Rehashing can consume the remaining positional uncertainty even when
        // the absolute 30-second cap has not elapsed.
        temporal = G3SparseFrameTimingPolicy.Evaluate(track.Timing, DateTimeOffset.UtcNow,
            G3SparseTimingPurpose.RegisteredCurrentFrame, field.TargetIdentification.CatalogPositionSpreadPixels,
            proof.MaximumPositionUncertaintyPixels, measuredDrift: solve.MeasuredDrift);
        return temporal.Usable ? integrity : GateResult.TargetQuality("G3_CATALOG_SHORT_POSITION_UNCONFIRMED",
            "Measured position expired while checking its source chain; retain all evidence and use the original recovery/skip path.");
    }
}
