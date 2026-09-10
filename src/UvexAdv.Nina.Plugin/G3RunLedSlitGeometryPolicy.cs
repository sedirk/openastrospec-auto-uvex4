using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

internal sealed record G3RunLedSlitGeometryContext(
    string ObservationRunId, string ActionConfigurationSha256,
    string CommissioningPresetSha256, string NightSetupSha256,
    string CameraStableId, int Binning, int ImageWidth, int ImageHeight,
    int SlitPosition, double SlitWidthMicrometers, long ConnectionEpoch,
    bool FullFrameOrigin, bool UvexMatchesSetup, bool LedConfirmedOff,
    bool FocusStableAcrossFrame, string SourceFrameSha256,
    string IdentityEvidenceSha256, DateTimeOffset FrameCompletedUtc);

/// <summary>
/// Bind a NEW target/guide frame to this run's independently measured LED slit.
/// Darkness in the unilluminated stellar frame is not an acceptance condition.
/// This never imports a historical overlay or supplies a target/guide centroid.
/// </summary>
internal static class G3RunLedSlitGeometryPolicy
{
    internal static SlitLocusDetection Evaluate(G3SlitGeometryRunCache cache, G3RunLedSlitGeometryContext current)
    {
        var valid = cache.PairAnalysis.Gate.Disposition == GateDisposition.Passed &&
            cache.SlitDetection.Gate.Disposition == GateDisposition.Passed &&
            cache.SlitIdentity.Gate.Disposition == GateDisposition.Passed &&
            cache.ObservationRunId == current.ObservationRunId &&
            Hash(cache.ActionConfigurationSha256, current.ActionConfigurationSha256) &&
            Hash(cache.CommissioningPresetSha256, current.CommissioningPresetSha256) &&
            Hash(cache.NightSetupSha256, current.NightSetupSha256) &&
            Hash(cache.SourceFrameSha256, current.SourceFrameSha256) &&
            Hash(cache.SlitIdentityEvidenceSha256, current.IdentityEvidenceSha256) &&
            !string.IsNullOrWhiteSpace(cache.SlitIdentityEvidencePath) &&
            !string.IsNullOrWhiteSpace(cache.SourceFramePath) &&
            string.Equals(cache.G3CameraStableId, current.CameraStableId, StringComparison.OrdinalIgnoreCase) &&
            cache.Binning == current.Binning && cache.ImageWidthPixels == current.ImageWidth &&
            cache.ImageHeightPixels == current.ImageHeight && current.FullFrameOrigin &&
            cache.SlitPosition == current.SlitPosition &&
            double.IsFinite(current.SlitWidthMicrometers) &&
            Math.Abs(cache.SlitWidthMicrometers - current.SlitWidthMicrometers) <= 0.01 &&
            cache.Phd2ConnectionEpoch == current.ConnectionEpoch &&
            current.UvexMatchesSetup && current.LedConfirmedOff && current.FocusStableAcrossFrame &&
            cache.CapturedUtc <= current.FrameCompletedUtc && current.FrameCompletedUtc <= DateTimeOffset.UtcNow;
        var geometry = cache.SlitDetection.Geometry;
        valid &= double.IsFinite(geometry.AcquisitionPoint.X) && double.IsFinite(geometry.AcquisitionPoint.Y) &&
            geometry.AcquisitionPoint.X >= 0 && geometry.AcquisitionPoint.X < current.ImageWidth &&
            geometry.AcquisitionPoint.Y >= 0 && geometry.AcquisitionPoint.Y < current.ImageHeight &&
            double.IsFinite(geometry.AngleDegrees) && double.IsFinite(geometry.LengthPixels) &&
            double.IsFinite(geometry.WidthPixels) && geometry.LengthPixels > 0 && geometry.WidthPixels > 0 &&
            string.Equals(geometry.CameraIdentity, current.CameraStableId, StringComparison.OrdinalIgnoreCase) &&
            geometry.BinningX == current.Binning && geometry.BinningY == current.Binning;
        var gate = valid
            ? GateResult.Pass("G3_RUN_LED_SLIT_GEOMETRY_VALID",
                "This fresh target/guide frame is referenced to the same run's hash-verified LED OFF/ON/OFF physical slit; stellar-frame dark-line visibility is not required.",
                new Dictionary<string, double>
                {
                    ["ledMeasuredContrastSigma"] = cache.SlitDetection.ContrastSigma,
                    ["ledReferenceAgeSeconds"] = (current.FrameCompletedUtc - cache.CapturedUtc).TotalSeconds,
                    ["stellarFrameSlitDetectionRequired"] = 0,
                    ["targetOrGuidePositionReplaced"] = 0,
                })
            : GateResult.Unknown("G3_RUN_LED_SLIT_GEOMETRY_INVALID",
                "The run LED slit measurement is missing a valid source/identity/geometry/device-state binding. Reacquire LED geometry at a safe acquisition boundary; do not substitute a remembered pixel or an unilluminated-frame guess.");
        return cache.SlitDetection with { Gate = gate };
    }

    private static bool Hash(string a, string b) =>
        a is { Length: 64 } && a.All(Uri.IsHexDigit) &&
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
