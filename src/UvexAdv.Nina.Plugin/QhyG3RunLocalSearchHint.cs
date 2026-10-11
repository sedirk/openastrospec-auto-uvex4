using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

internal sealed record QhyG3SearchHintBinding(
    string RunId, string ActionSha256, string PresetSha256, string NightSetupId,
    string NightSetupSha256, string InstallationEpochId, string TelescopeId,
    string QhyCameraId, string G3CameraId, string PierSide, int G3Binning);

/// <summary>A local solver search center only; never a WCS or movement authority.</summary>
internal sealed record QhyG3RunLocalSearchHint(double RightAscensionDegrees,
    double DeclinationDegrees, double SourcePairUncertaintyArcseconds, double DistanceFromPairArcseconds)
{
    public bool MotionAuthority => false;
    public bool MeasuredTarget => false;

    internal static QhyG3RunLocalSearchHint? TryProject(QhyToG3TransferCandidate candidate,
        QhyG3SolvedFrame currentQhy, QhyG3SearchHintBinding binding,
        double maximumLocalOffsetDegrees, DateTimeOffset now, out string reason)
    {
        reason = "QHY_G3_SEARCH_HINT_BINDING_INAPPLICABLE";
        if (candidate.ValidateIntegrity().Count != 0 ||
            candidate.ObservationRunId != binding.RunId ||
            !SameHash(candidate.ActionConfigurationSha256, binding.ActionSha256) ||
            !SameHash(candidate.CommissioningPresetSha256, binding.PresetSha256) ||
            candidate.NightSetupId != binding.NightSetupId ||
            !SameHash(candidate.NightSetupSha256, binding.NightSetupSha256) ||
            candidate.InstallationEpochId != binding.InstallationEpochId ||
            candidate.TelescopeDeviceId != binding.TelescopeId ||
            candidate.QhyCameraStableId != binding.QhyCameraId || currentQhy.CameraStableId != binding.QhyCameraId ||
            candidate.G3CameraStableId != binding.G3CameraId ||
            !string.Equals(candidate.PierSide, binding.PierSide, StringComparison.OrdinalIgnoreCase) ||
            candidate.G3.BinningX != binding.G3Binning || candidate.G3.BinningY != binding.G3Binning)
            return null;
        reason = "QHY_G3_SEARCH_HINT_STALE";
        if (now < candidate.ValidFromUtc || now > candidate.ValidUntilUtc ||
            currentQhy.ExposureMidpointUtc > now ||
            now - currentQhy.ExposureMidpointUtc > candidate.CollectionPolicy.MaximumCachedQhyAge ||
            currentQhy.SolveCompletedUtc > now || currentQhy.SolveCompletedUtc < currentQhy.ExposureEndedUtc)
            return null;
        var source = candidate.Qhy;
        reason = "QHY_G3_SEARCH_HINT_GEOMETRY_CHANGED";
        if (currentQhy.FrameWidthPixels != source.FrameWidthPixels || currentQhy.FrameHeightPixels != source.FrameHeightPixels ||
            currentQhy.BinningX != source.BinningX || currentQhy.BinningY != source.BinningY ||
            currentQhy.RoiOriginX != source.RoiOriginX || currentQhy.RoiOriginY != source.RoiOriginY ||
            currentQhy.RoiWidthPixels != source.RoiWidthPixels || currentQhy.RoiHeightPixels != source.RoiHeightPixels ||
            currentQhy.Flipped != source.Flipped || !double.IsFinite(currentQhy.PositionAngleDegrees) ||
            !double.IsFinite(currentQhy.PixelScaleArcseconds) || currentQhy.PixelScaleArcseconds <= 0)
            return null;
        reason = "QHY_G3_SEARCH_HINT_OUTSIDE_LOCAL_FIELD";
        if (!double.IsFinite(maximumLocalOffsetDegrees) || maximumLocalOffsetDegrees <= 0 ||
            !double.IsFinite(currentQhy.CenterRightAscensionDegrees) || currentQhy.CenterRightAscensionDegrees is < 0 or >= 360 ||
            !double.IsFinite(currentQhy.CenterDeclinationDegrees) || currentQhy.CenterDeclinationDegrees is <= -90 or >= 90)
            return null;
        // A one-pair mapping is not a sky-wide calibration. Restrict even this
        // non-authoritative hint to one measured QHY field radius and the
        // existing configured solver radius, whichever is smaller.
        var fieldRadius = Math.Min(source.FrameWidthPixels, source.FrameHeightPixels) * source.PixelScaleArcseconds / 2;
        var localRadius = Math.Min(maximumLocalOffsetDegrees * 3600, fieldRadius);
        var separation = G3AcquisitionMotionPlanner.AngularSeparationArcseconds(
            source.CenterRightAscensionDegrees, source.CenterDeclinationDegrees,
            currentQhy.CenterRightAscensionDegrees, currentQhy.CenterDeclinationDegrees);
        if (!double.IsFinite(separation) || separation > localRadius) return null;
        // Rotation/scale changes must not move the measured offset by more
        // than its recorded uncertainty. Failure is fallback, not a run gate.
        var angle = Math.IEEERemainder(currentQhy.PositionAngleDegrees - source.PositionAngleDegrees, 360) * Math.PI / 180;
        var scale = Math.Abs(currentQhy.PixelScaleArcseconds / source.PixelScaleArcseconds - 1);
        var offset = candidate.Model.PredictedPrepositionMagnitudeArcseconds;
        if (Math.Abs(2 * offset * Math.Sin(angle / 2)) + offset * scale > candidate.Model.PredictionUncertaintyArcseconds)
        { reason = "QHY_G3_SEARCH_HINT_ORIENTATION_CHANGED"; return null; }
        var center = G3AcquisitionMotionPlanner.ApplyTangentOffsetArcseconds(
            currentQhy.CenterRightAscensionDegrees, currentQhy.CenterDeclinationDegrees,
            candidate.Model.G3MinusQhyEastArcseconds, candidate.Model.G3MinusQhyNorthArcseconds);
        reason = "QHY_G3_RUN_LOCAL_SEARCH_HINT";
        return new(center.RaDegrees, center.DecDegrees, candidate.Model.PredictionUncertaintyArcseconds, separation);
    }

    private static bool SameHash(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
