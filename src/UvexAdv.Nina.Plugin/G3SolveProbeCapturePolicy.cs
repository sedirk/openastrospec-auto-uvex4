using UvexAdv.Observatory;
using UvexAdv.Phd2;

namespace UvexAdv.Nina.Plugin;

/// <summary>
/// Validates what PHD2 can actually attest for a saved solve-only frame.
/// Loop/save probes use the hash-locked guiding profile for gain/binning.
/// Native capture_single_frame may instead attest an explicit per-frame gain;
/// that override never changes which profile is required for guiding.
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
        Phd2ProfileBindingSnapshot? lockedProfileEvidence,
        int? nativeCaptureGainPercent = null)
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
        if (nativeCaptureGainPercent.HasValue &&
            (nativeCaptureGainPercent.Value is < 0 or > 100 ||
             !captured.GainAndBinningApplied || captured.UsedLoopSaveFallback))
        {
            return GateResult.Unknown(
                "G3_SOLVE_PROBE_NATIVE_GAIN_UNATTESTED",
                "An explicit per-frame gain requires native parameterized capture; guiding-profile or loop/save evidence cannot attest it.");
        }
        var expectedFrameGain = nativeCaptureGainPercent ?? lockedGainPercent;
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
        if (fitsGain >= 0 && fitsGain != expectedFrameGain)
        {
            return GateResult.Fail(
                "G3_SOLVE_PROBE_GAIN_MISMATCH",
                $"G3 solve-only FITS reports gain {fitsGain}; expected capture gain is {expectedFrameGain}.");
        }

        return GateResult.Pass(
            "G3_SOLVE_PROBE_FRAME_VALID",
            nativeCaptureGainPercent.HasValue
                ? "Native PHD2 attests the per-frame exposure/gain/binning; available FITS headers agree and the original guiding profile remains hash-locked."
                : "The exposure is attested by PHD2 and FITS; gain/binning are attested by the hash-locked Windows PHD2 profile and match FITS when those headers are exposed.");
    }
}
