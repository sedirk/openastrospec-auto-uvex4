using UvexAdv.Observatory;
using UvexAdv.Phd2;

namespace UvexAdv.Nina.Plugin;

/// <summary>
/// Validates what PHD2 can actually attest for a saved solve-only frame.
/// PHD2's JSON-RPC applies exposure, but cannot apply gain or binning per
/// request.  Those two values therefore come from the already validated,
/// hash-locked Windows profile and are cross-checked against FITS metadata when
/// N.I.N.A. exposes the corresponding headers.
/// </summary>
internal static class G3SolveProbeCapturePolicy
{
    public static GateResult Validate(
        Phd2SingleFrameResult captured,
        double fitsExposureMilliseconds,
        int fitsBinX,
        int fitsBinY,
        int fitsGain,
        int requestedExposureMilliseconds,
        int lockedBinning,
        int lockedGainPercent,
        Phd2ProfileBindingSnapshot? lockedProfileEvidence)
    {
        ArgumentNullException.ThrowIfNull(captured);
        if (!captured.ExposureApplied)
        {
            return GateResult.Unknown(
                "G3_SOLVE_PROBE_EXPOSURE_NOT_APPLIED",
                "PHD2 did not attest that the requested solve-only exposure was applied.");
        }
        if (lockedProfileEvidence is null ||
            string.IsNullOrWhiteSpace(lockedProfileEvidence.Sha256) ||
            string.IsNullOrWhiteSpace(lockedProfileEvidence.EvidenceSource) ||
            lockedProfileEvidence.Binning != lockedBinning ||
            lockedProfileEvidence.GainPercent != lockedGainPercent)
        {
            return GateResult.Unknown(
                "G3_SOLVE_PROBE_PROFILE_PARAMETERS_UNATTESTED",
                "Hash-locked Windows PHD2 profile evidence does not attest the configured G3 gain and binning.");
        }
        if (!double.IsFinite(fitsExposureMilliseconds))
        {
            return GateResult.Unknown(
                "G3_SOLVE_PROBE_EXPOSURE_MISSING",
                "The solve-only FITS exposure metadata is missing or invalid.");
        }
        var exposureDelta = Math.Abs(fitsExposureMilliseconds - requestedExposureMilliseconds);
        if (exposureDelta > Math.Max(10, requestedExposureMilliseconds * 0.02))
        {
            return GateResult.Unknown(
                "G3_SOLVE_PROBE_EXPOSURE_MISMATCH",
                $"G3 solve-only FITS exposure {fitsExposureMilliseconds:F0} ms does not match requested {requestedExposureMilliseconds} ms.");
        }
        if (fitsBinX > 0 && (fitsBinX != lockedBinning || fitsBinY != lockedBinning))
        {
            return GateResult.Fail(
                "G3_SOLVE_PROBE_BINNING_MISMATCH",
                $"G3 solve-only FITS reports {fitsBinX}x{fitsBinY}; locked profile binning is {lockedBinning}x{lockedBinning}.");
        }
        if (fitsGain >= 0 && fitsGain != lockedGainPercent)
        {
            return GateResult.Fail(
                "G3_SOLVE_PROBE_GAIN_MISMATCH",
                $"G3 solve-only FITS reports gain {fitsGain}; locked PHD2 profile gain is {lockedGainPercent}.");
        }

        return GateResult.Pass(
            "G3_SOLVE_PROBE_FRAME_VALID",
            "The exposure is attested by PHD2 and FITS; gain/binning are attested by the hash-locked Windows PHD2 profile and match FITS when those headers are exposed.");
    }
}
