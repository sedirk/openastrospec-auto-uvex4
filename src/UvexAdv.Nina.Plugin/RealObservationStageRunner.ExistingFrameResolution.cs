using NINA.Astrometry;
using NINA.Image.Interfaces;
using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

internal sealed partial class RealObservationStageRunner
{
    private Task<PlateSolveEvidence> SolveG3HandoffFrameWithResolutionRecoveryAsync(
        ObservationContext context, IImageData image, Coordinates hint, string role,
        string sourcePath, G3FieldMountBinding binding, bool outsideFineHandoff,
        int? firstOverride, CancellationToken cancellationToken)
    {
        var requestedFactor = firstOverride ?? PlateSolveDownSamplePolicy.EffectiveForRole(
            configuration.PlateSolver.DownSampleFactor, role);
        var actualFactor = PlateSolveSoftwareBinning.SelectFactor(role, solverIdentity, requestedFactor,
            image.Properties.Width, image.Properties.Height, image.Properties.IsBayered);
        // A post-WCS handoff already has the original outstanding return clock.
        // Do not extend this optional resolution path to initial/unbounded work.
        var eligible = outsideFineHandoff && durableG3AcquisitionMotion is
            { Phase: G3AcquisitionMotionPhase.AwaitingFreshSolve };
        return G3ExistingFrameResolutionRecovery.SolveAsync(eligible, actualFactor, firstOverride,
            (factor, token) => SolveImageAsync(image, configuration.G3.FocalLengthMillimeters,
                configuration.G3.PixelSizeMicrometers, configuration.G3.Binning, hint,
                role, sourcePath, token, softwareDownSampleOverride: factor),
            completed =>
            {
                if (!completed.Result.Success || completed.Result.Coordinates is null)
                    return G3CompletedSolveResolutionResult.NoSolution;
                // A formal parity mismatch invalidates commissioning. Never
                // replace it with another resolution in search of a passing WCS.
                if (completed.Result.Flipped != configuration.G3.ExpectedWcsFlipped)
                    return G3CompletedSolveResolutionResult.ParityMismatch;
                return ValidateG3PlateSolvePlausibility(completed, image.Properties.Width, image.Properties.Height)
                    .Disposition == GateDisposition.Passed
                    ? G3CompletedSolveResolutionResult.Accepted
                    : G3CompletedSolveResolutionResult.Implausible;
            },
            async (first, token) =>
            {
                await RequireImmediatePhysicalActionGatesAsync(context, token).ConfigureAwait(false);
                var owner = await phd2.ValidateIdentityAsync(PhdIdentityRequirement(), token).ConfigureAwait(false);
                if (!owner.IsValid)
                    throw new PhysicalActionGateException(GateResult.Unknown("PHD2_IDENTITY_STALE",
                        string.Join(" ", owner.Failures.Concat(owner.IndeterminateReasons))));
                var profile = ValidatePhdProfileBindingEvidence();
                if (profile.Disposition != GateDisposition.Passed) throw new PhysicalActionGateException(profile);
                var frame = G3FieldState.Failed(GateResult.Pass("G3_EXISTING_FRAME_RESOLUTION_CHECK", "Read-only existing-frame check."),
                    sourcePath, image, first, binding);
                var currentBinding = await ValidateG3FieldMountBindingForMotionAsync(context, frame, token).ConfigureAwait(false);
                if (currentBinding.Disposition != GateDisposition.Passed) throw new PhysicalActionGateException(currentBinding);
                await PublishRunJsonEvidenceAsync("g3-existing-frame-native-resolution",
                    "One raw-resolution solve of the existing outside-handoff frame",
                    new { firstSolveEvidence = first.EvidencePath, sourcePath, binding.FrameSha256,
                        binding.BindingSha256, priorSoftwareFactor = actualFactor, nextSoftwareFactor = 1,
                        exposureIssued = false, motionIssued = false, budgetReset = false,
                        originalMotionClockUtc = durableG3AcquisitionMotion?.MotionClockStartedUtc,
                        originalMaximumElapsedSeconds = durableG3AcquisitionMotion?.MaximumElapsedSeconds,
                        returnTimeGuard = "RunWithG3PendingReturnTimeAsync rechecks the unchanged ledger before each solve" },
                    sourcePath, token).ConfigureAwait(false);
                // SolveImageAsync invokes RunWithG3PendingReturnTimeAsync again:
                // elapsed time from BOTH invocations consumes the same clock,
                // original segmented return and cleanup reserve remain intact.
            }, cancellationToken);
    }
}
