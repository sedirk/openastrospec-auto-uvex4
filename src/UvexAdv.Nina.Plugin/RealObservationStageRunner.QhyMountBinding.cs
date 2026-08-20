using System.IO;
using UvexAdv.Observatory;
using UvexAdv.Qhy.Core;

namespace UvexAdv.Nina.Plugin;

internal sealed partial class RealObservationStageRunner
{
    private QhyAcceptedFrameMountBinding CreateQhyAcceptedFrameMountBinding(
        ObservationContext context,
        QhyJobSnapshot job,
        QhyFrameRecord accepted,
        G3FrameMountReadback beforeJob,
        G3FrameMountReadback afterAcceptedFrame)
    {
        if (commissioning is null) throw new InvalidOperationException("Commissioning preset is not loaded.");
        return QhyAcceptedFrameMountBinding.Create(
            context.Plan.ObservationRunId,
            configuration.ActionConfigurationSha256,
            commissioning.Sha256,
            job.Id,
            accepted.FrameId,
            accepted.Sha256,
            accepted.ExposureStartedUtc,
            accepted.ExposureEndedUtc,
            beforeJob,
            afterAcceptedFrame);
    }

    private async Task<GateResult> ValidateQhyAcceptedFrameMountBindingForMotionAsync(
        ObservationContext context,
        QhyJobSnapshot job,
        QhyFrameRecord accepted,
        PlateSolveEvidence solve,
        CancellationToken cancellationToken)
    {
        if (commissioning is null)
            return GateResult.Unknown("COMMISSIONING_PRESET_REQUIRED", "Commissioning is unavailable while validating the QHY capture mount binding.");
        var binding = lastQhyAcceptedFrameMountBinding;
        if (binding is null)
            return GateResult.Unknown("QHY_CAPTURE_MOUNT_BINDING_MISSING", "The accepted QHY frame lacks pre-job/post-frame mount readbacks; no centering or ghost-derived motion is authorized.");
        var integrity = binding.Validate(
            context.Plan.ObservationRunId,
            configuration.ActionConfigurationSha256,
            commissioning.Sha256,
            job.Id,
            accepted.FrameId,
            accepted.Sha256,
            MountCommandArrivalToleranceArcseconds);
        if (integrity.Disposition != GateDisposition.Passed) return integrity;
        var solveBinding = lastQhySolveMountBinding;
        try
        {
            if (!solve.Result.Success || solve.Result.Coordinates is null ||
                solveBinding is null ||
                !string.Equals(Path.GetFullPath(solve.SourcePath), Path.GetFullPath(accepted.FitsPath), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetFullPath(solveBinding.SolveEvidencePath), Path.GetFullPath(solve.EvidencePath), StringComparison.OrdinalIgnoreCase) ||
                !SameHash(solveBinding.SolveEvidenceSha256, solve.EvidenceSha256) ||
                solveBinding.FrameId != accepted.FrameId ||
                !SameHash(solveBinding.FrameSha256, accepted.Sha256) ||
                solveBinding.ExposureEndedUtc != accepted.ExposureEndedUtc ||
                !SameHash(solveBinding.CaptureBindingSha256, binding.BindingSha256) ||
                solveBinding.RightAscensionDegrees != binding.AfterAcceptedFrame.RightAscensionDegrees ||
                solveBinding.DeclinationDegrees != binding.AfterAcceptedFrame.DeclinationDegrees ||
                !string.Equals(solveBinding.CoordinateEpoch, binding.AfterAcceptedFrame.CoordinateEpoch, StringComparison.Ordinal) ||
                !string.Equals(solveBinding.PierSide, binding.AfterAcceptedFrame.PierSide, StringComparison.OrdinalIgnoreCase) ||
                solveBinding.CapturedUtc != binding.AfterAcceptedFrame.ReportedUtc)
            {
                return GateResult.Unknown(
                    "QHY_SOLVE_CAPTURE_BINDING_CHANGED",
                    "The QHY solve is not exactly bound to the current accepted frame, dual-ended capture attestation and immutable solve evidence.");
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return GateResult.Unknown("QHY_SOLVE_CAPTURE_PATH_INVALID", $"The QHY solve/frame path cannot be canonicalized: {ex.Message}");
        }
        if (!File.Exists(accepted.FitsPath))
            return GateResult.Unknown("QHY_CAPTURE_BOUND_FRAME_MISSING", "The accepted QHY FITS no longer exists.");
        if (!File.Exists(solve.EvidencePath))
            return GateResult.Unknown("QHY_CAPTURE_BOUND_SOLVE_MISSING", "The WCS evidence bound to the accepted QHY FITS no longer exists.");
        try
        {
            var actualSha = await ComputeFileSha256Async(accepted.FitsPath, cancellationToken).ConfigureAwait(false);
            var actualSolveSha = await ComputeFileSha256Async(solve.EvidencePath, cancellationToken).ConfigureAwait(false);
            if (!SameHash(actualSha, accepted.Sha256))
                return GateResult.Fail("QHY_CAPTURE_BOUND_FRAME_CHANGED", "The accepted QHY FITS hash differs from the immutable frame manifest.");
            if (!SameHash(actualSolveSha, solve.EvidenceSha256))
                return GateResult.Fail("QHY_CAPTURE_BOUND_SOLVE_CHANGED", "The QHY WCS evidence hash differs from the solve attestation used to plan centering.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return GateResult.Unknown("QHY_CAPTURE_BOUND_FRAME_UNREADABLE", $"The accepted QHY FITS could not be re-hashed: {ex.Message}");
        }
        G3FrameMountReadback current;
        try { current = CaptureG3FrameMountReadback(); }
        catch (Exception ex)
        {
            return GateResult.Unknown("QHY_CAPTURE_CURRENT_MOUNT_UNAVAILABLE", $"A fresh mount readback is unavailable: {ex.Message}");
        }
        if (!string.Equals(current.CoordinateEpoch, binding.AfterAcceptedFrame.CoordinateEpoch, StringComparison.Ordinal) ||
            !string.Equals(current.PierSide, binding.AfterAcceptedFrame.PierSide, StringComparison.OrdinalIgnoreCase))
            return GateResult.Unknown("QHY_CAPTURE_CURRENT_TOPOLOGY_CHANGED", "Mount epoch or pier side changed after the accepted QHY frame.");
        var separation = G3AcquisitionMotionPlanner.AngularSeparationArcseconds(
            binding.AfterAcceptedFrame.RightAscensionDegrees,
            binding.AfterAcceptedFrame.DeclinationDegrees,
            current.RightAscensionDegrees,
            current.DeclinationDegrees);
        if (!double.IsFinite(separation) || separation > MountCommandArrivalToleranceArcseconds + 1e-9)
            return GateResult.Unknown(
                "QHY_CAPTURE_MOUNT_BINDING_STALE",
                $"The fresh mount report is {separation:F2} arcsec from the accepted QHY capture (limit {MountCommandArrivalToleranceArcseconds:F2}); discard and reacquire before centering.");
        return GateResult.Pass(
            "QHY_CAPTURE_MOUNT_BINDING_FRESH",
            $"The accepted QHY frame remains bound to the current mount position within {separation:F2} arcsec.");
    }
}
