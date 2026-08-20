using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO;
using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

internal sealed record RealCommissioningPreset(
    int SchemaVersion,
    string PresetId,
    DateTimeOffset CreatedUtc,
    string Provenance,
    string NightSetupId,
    string NightSetupSha256,
    string Phd2ProfileEvidenceSha256,
    string Phd2CalibrationTimestampUtc,
    string TelescopeDeviceId,
    string G3CameraStableId,
    int G3Binning,
    int G3ExposureMilliseconds,
    int G3GainPercent,
    bool G3ExpectedWcsFlipped,
    SlitGeometryPreset Slit,
    MountTransformPreset MountTransform,
    MotionLimitPreset Motion,
    EnvironmentPreset Environment,
    DateTimeOffset? ValidUntilUtc = null,
    HardwareFingerprintPreset? HardwareFingerprint = null,
    int G3SaturationAdu = UvexPluginSettings.G3M2210mDefaultSaturationAdu,
    RealSlitPlacementAuthority FineMotionAuthority = RealSlitPlacementAuthority.IndependentMountTransform,
    Phd2SlitPlacementCommissioningPreset? Phd2SlitPlacement = null,
    GhostAssistanceCommissioningPreset? GhostAssistance = null,
    SlitWheelIdentityCalibration? SlitWheelIdentity = null);

internal sealed record HardwareFingerprintPreset(
    string AtrCameraStableId,
    string G3CameraStableId,
    string QhyCameraStableId,
    string TelescopeDeviceId,
    string NightSetupId,
    string NightSetupSha256,
    string Phd2ProfileEvidenceSha256,
    string Sha256);

internal sealed record SlitGeometryPreset(
    string CalibrationId,
    double AcquisitionX,
    double AcquisitionY,
    double AngleDegrees,
    double LengthPixels,
    double WidthPixels,
    double UncertaintyPixels);

internal sealed record MountTransformPreset(
    string CalibrationId,
    string PierSide,
    double RaArcsecondsPerPixelX,
    double RaArcsecondsPerPixelY,
    double DecArcsecondsPerPixelX,
    double DecArcsecondsPerPixelY,
    double RmsArcseconds);

internal sealed record MotionLimitPreset(
    double MaximumSingleCorrectionArcseconds,
    double MaximumCumulativeCorrectionArcseconds,
    int MaximumCorrectionAttempts,
    double MaximumAcquisitionMinutes);

internal sealed record EnvironmentPreset(
    bool RequireSafetyMonitor,
    bool RequireOpenDomeOrRoof,
    bool RequireWeatherData,
    double MaximumCloudCoverPercent,
    double MaximumHumidityPercent,
    double MaximumWindSpeedMetersPerSecond);

internal sealed record LoadedCommissioningPreset(
    RealCommissioningPreset Value,
    string AbsolutePath,
    string Sha256,
    SlitGeometry SlitGeometry,
    PixelToMountTransform MountTransform,
    MotionLimits MotionLimits);

internal static class RealCommissioningPresetLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static async Task<(LoadedCommissioningPreset? Preset, IReadOnlyList<string> Issues)> LoadAsync(
        RealRunConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var binding = configuration.Commissioning;
        var issues = new List<string>();
        if (!binding.RealModeCommissioned) issues.Add("Real mode is not marked commissioned.");
        if (!binding.SlitGeometryCommissioned) issues.Add("Slit geometry is not marked commissioned.");
        if (string.IsNullOrWhiteSpace(binding.PresetPath)) issues.Add("Commissioning preset path is required.");
        if (string.IsNullOrWhiteSpace(binding.PresetId)) issues.Add("Commissioning preset ID is required.");
        if (string.IsNullOrWhiteSpace(binding.PresetSha256)) issues.Add("Commissioning preset SHA-256 is required.");
        if (string.IsNullOrWhiteSpace(binding.HardwareFingerprintSha256)) issues.Add("Commissioning hardware-fingerprint SHA-256 is required.");
        if (issues.Count > 0) return (null, issues);

        string absolutePath;
        try { absolutePath = Path.GetFullPath(binding.PresetPath); }
        catch (Exception ex) { return (null, [$"Commissioning preset path is invalid: {ex.Message}"]); }
        if (!File.Exists(absolutePath)) return (null, [$"Commissioning preset does not exist: {absolutePath}"]);

        byte[] bytes;
        try { bytes = await File.ReadAllBytesAsync(absolutePath, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) { return (null, [$"Commissioning preset could not be read: {ex.Message}"]); }
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes));
        if (!string.Equals(sha256, NormalizeHash(binding.PresetSha256), StringComparison.OrdinalIgnoreCase))
        {
            return (null, [$"Commissioning preset SHA-256 mismatch. Actual {sha256}."]);
        }

        RealCommissioningPreset? preset;
        try { preset = JsonSerializer.Deserialize<RealCommissioningPreset>(bytes, JsonOptions); }
        catch (Exception ex) { return (null, [$"Commissioning preset JSON is invalid: {ex.Message}"]); }
        if (preset is null) return (null, ["Commissioning preset JSON is empty."]);

        ValidatePreset(configuration, preset, issues);
        if (issues.Count > 0) return (null, issues);

        var slit = new SlitGeometry(
            preset.Slit.CalibrationId,
            new PixelPoint(preset.Slit.AcquisitionX, preset.Slit.AcquisitionY),
            preset.Slit.AngleDegrees,
            preset.Slit.LengthPixels,
            preset.Slit.WidthPixels,
            preset.Slit.UncertaintyPixels,
            preset.G3CameraStableId,
            preset.G3Binning,
            preset.G3Binning);
        var transform = new PixelToMountTransform(
            preset.MountTransform.CalibrationId,
            preset.MountTransform.RaArcsecondsPerPixelX,
            preset.MountTransform.RaArcsecondsPerPixelY,
            preset.MountTransform.DecArcsecondsPerPixelX,
            preset.MountTransform.DecArcsecondsPerPixelY,
            preset.MountTransform.PierSide,
            preset.MountTransform.RmsArcseconds,
            preset.CreatedUtc);
        var motion = new MotionLimits(
            preset.Motion.MaximumSingleCorrectionArcseconds / 3600d,
            preset.Motion.MaximumCumulativeCorrectionArcseconds / 3600d,
            preset.Motion.MaximumCorrectionAttempts,
            TimeSpan.FromMinutes(preset.Motion.MaximumAcquisitionMinutes));
        return (new LoadedCommissioningPreset(preset, absolutePath, sha256, slit, transform, motion), issues);
    }

    private static void ValidatePreset(RealRunConfiguration configuration, RealCommissioningPreset preset, List<string> issues)
    {
        var binding = configuration.Commissioning;
        if (preset.SchemaVersion != 4)
            issues.Add($"Commissioning preset schema {preset.SchemaVersion} is not authorized for automatic real science; schema 4 with complete PHD2 slit-placement and four-slot optical slit-identity bindings is required.");
        if (!Enum.IsDefined(preset.FineMotionAuthority)) issues.Add("Commissioning fine-motion authority is invalid.");
        if (preset.Phd2SlitPlacement is null)
            issues.Add("Schema 4 requires hash-bound PHD2 guide exposure, topology, calibration-quality and bounded-return commissioning for every automatic real-science route.");
        else
            issues.AddRange(preset.Phd2SlitPlacement.Validate());
        if (preset.FineMotionAuthority is RealSlitPlacementAuthority.Phd2CalibrationLockShift or RealSlitPlacementAuthority.AutoPreferPhd2ThenIndependent)
        {
            if (preset.FineMotionAuthority == RealSlitPlacementAuthority.AutoPreferPhd2ThenIndependent && !binding.MountTransformCommissioned)
                issues.Add("Auto PHD2-to-independent fallback requires a separately commissioned pixel-to-mount transform.");
        }
        else if (!binding.MountTransformCommissioned)
        {
            issues.Add("Pixel-to-mount transform is not marked commissioned for the selected fine-motion authority.");
        }
        if (!string.Equals(preset.PresetId, binding.PresetId, StringComparison.Ordinal)) issues.Add("Commissioning preset ID does not match the locked run binding.");
        if (preset.CreatedUtc == default) issues.Add("Commissioning preset creation timestamp is missing.");
        if (preset.CreatedUtc > DateTimeOffset.UtcNow.AddMinutes(5)) issues.Add("Commissioning preset creation timestamp is in the future.");
        if (preset.ValidUntilUtc is not { } validUntil) issues.Add("Commissioning preset validity deadline is missing.");
        else
        {
            if (validUntil <= preset.CreatedUtc) issues.Add("Commissioning preset validity deadline must be after creation.");
            if (validUntil <= DateTimeOffset.UtcNow) issues.Add($"Commissioning preset expired at {validUntil:O}.");
        }
        if (string.IsNullOrWhiteSpace(preset.Provenance)) issues.Add("Commissioning preset provenance is missing.");
        if (!string.Equals(preset.NightSetupId, configuration.NightSetup.NightSetupId, StringComparison.Ordinal)) issues.Add("Commissioning preset Night Setup ID mismatch.");
        if (!string.Equals(NormalizeHash(preset.NightSetupSha256), NormalizeHash(configuration.NightSetup.SnapshotSha256), StringComparison.OrdinalIgnoreCase)) issues.Add("Commissioning preset Night Setup SHA-256 mismatch.");
        if (!string.Equals(NormalizeHash(preset.Phd2ProfileEvidenceSha256), NormalizeHash(configuration.Phd2.ProfileEvidenceSha256), StringComparison.OrdinalIgnoreCase)) issues.Add("Commissioning preset PHD2 profile-evidence SHA-256 mismatch.");
        if (!string.Equals(preset.Phd2CalibrationTimestampUtc, configuration.Phd2.CalibrationTimestampUtc, StringComparison.Ordinal)) issues.Add("Commissioning preset PHD2 calibration timestamp mismatch.");
        if (!string.Equals(preset.TelescopeDeviceId, configuration.ExpectedTelescopeId, StringComparison.Ordinal)) issues.Add("Commissioning preset telescope identity mismatch.");
        if (!string.Equals(preset.G3CameraStableId, configuration.Phd2.CameraStableId, StringComparison.Ordinal)) issues.Add("Commissioning preset G3 identity mismatch.");
        if (preset.G3Binning != configuration.G3.Binning) issues.Add("Commissioning preset G3 binning mismatch.");
        if (preset.G3ExposureMilliseconds != configuration.G3.ExposureMilliseconds) issues.Add("Commissioning preset G3 exposure mismatch.");
        if (preset.G3GainPercent != configuration.G3.GainPercent) issues.Add("Commissioning preset G3 gain mismatch.");
        if (preset.G3ExpectedWcsFlipped != configuration.G3.ExpectedWcsFlipped) issues.Add("Commissioning preset G3 WCS parity mismatch.");
        if (preset.G3SaturationAdu != configuration.G3.SaturationAdu) issues.Add("Commissioning preset G3 saturation ADU mismatch.");
        if (preset.G3SaturationAdu is <= 0 or > ushort.MaxValue) issues.Add("Commissioning preset G3 saturation ADU is invalid.");

        if (configuration.G3.GhostAssistanceMode != GhostAssistanceMode.Skip)
        {
            if (preset.SchemaVersion != 4)
                issues.Add("Ghost assistance requires commissioning preset schema 4.");
            if (preset.GhostAssistance is null)
                issues.Add("The selected ghost-assistance mode requires a complete versioned calibration, match policy, extraction policy and runtime fingerprint in the commissioning preset.");
            else
                issues.AddRange(preset.GhostAssistance.Validate());
        }

        ValidateHardwareFingerprint(configuration, preset, issues);

        RequireSame(issues, "slit X", preset.Slit.AcquisitionX, binding.SlitSeedX);
        RequireSame(issues, "slit Y", preset.Slit.AcquisitionY, binding.SlitSeedY);
        RequireSame(issues, "slit angle", preset.Slit.AngleDegrees, binding.SlitAngleDegrees);
        RequireSame(issues, "slit length", preset.Slit.LengthPixels, binding.SlitLengthPixels);
        RequireSame(issues, "slit width", preset.Slit.WidthPixels, binding.SlitWidthPixels);
        RequireSame(issues, "slit uncertainty", preset.Slit.UncertaintyPixels, binding.SlitUncertaintyPixels);
        if (!string.Equals(preset.Slit.CalibrationId, binding.SlitGeometryCalibrationId, StringComparison.Ordinal)) issues.Add("Slit calibration ID mismatch.");
        if (preset.Slit.LengthPixels <= 0 || preset.Slit.WidthPixels <= 0 || preset.Slit.UncertaintyPixels < 0) issues.Add("Commissioned slit geometry has invalid dimensions.");

        if (preset.SlitWheelIdentity is null)
        {
            issues.Add("Schema 4 requires four independently measured LED slit-width fingerprints; the mechanical wheel ordinal cannot establish physical slit identity by itself.");
        }
        else
        {
            issues.AddRange(preset.SlitWheelIdentity.Validate());
            if (!string.Equals(preset.SlitWheelIdentity.CameraStableId, preset.G3CameraStableId, StringComparison.OrdinalIgnoreCase))
                issues.Add("Slit-wheel identity G3 camera does not match the commissioning preset.");
            if (preset.SlitWheelIdentity.BinningX != preset.G3Binning || preset.SlitWheelIdentity.BinningY != preset.G3Binning)
                issues.Add("Slit-wheel identity binning does not match the commissioning preset.");
            if (preset.Phd2SlitPlacement is { } placement &&
                !string.Equals(preset.SlitWheelIdentity.InstallationEpochId, placement.InstallationEpochId, StringComparison.Ordinal))
                issues.Add("Slit-wheel identity installation epoch does not match the PHD2/G3 optical-topology installation epoch.");

            foreach (var expected in UvexSlitWheelLayout.DeclaredSlots)
            {
                var actual = preset.SlitWheelIdentity.Fingerprints?.FirstOrDefault(item => item.WheelPosition == expected.WheelPosition);
                if (actual is null || Math.Abs(actual.NominalWidthMicrometers - expected.NominalWidthMicrometers) > 0.01)
                    issues.Add($"Slit-wheel identity position {expected.WheelPosition} must be operator-verified as {expected.NominalWidthMicrometers:F0} µm for this UVEX4 installation.");
            }
        }

        if (preset.FineMotionAuthority is RealSlitPlacementAuthority.IndependentMountTransform or RealSlitPlacementAuthority.AutoPreferPhd2ThenIndependent)
        {
            RequireSame(issues, "mount RA/X", preset.MountTransform.RaArcsecondsPerPixelX, binding.MountRaArcsecondsPerPixelX);
            RequireSame(issues, "mount RA/Y", preset.MountTransform.RaArcsecondsPerPixelY, binding.MountRaArcsecondsPerPixelY);
            RequireSame(issues, "mount Dec/X", preset.MountTransform.DecArcsecondsPerPixelX, binding.MountDecArcsecondsPerPixelX);
            RequireSame(issues, "mount Dec/Y", preset.MountTransform.DecArcsecondsPerPixelY, binding.MountDecArcsecondsPerPixelY);
            RequireSame(issues, "mount transform RMS", preset.MountTransform.RmsArcseconds, binding.MountTransformRmsArcseconds);
            if (!string.Equals(preset.MountTransform.CalibrationId, binding.MountTransformCalibrationId, StringComparison.Ordinal)) issues.Add("Mount transform calibration ID mismatch.");
            if (!string.Equals(preset.MountTransform.PierSide, binding.MountTransformPierSide, StringComparison.OrdinalIgnoreCase)) issues.Add("Mount transform pier-side binding mismatch.");
            var determinant = preset.MountTransform.RaArcsecondsPerPixelX * preset.MountTransform.DecArcsecondsPerPixelY
                - preset.MountTransform.RaArcsecondsPerPixelY * preset.MountTransform.DecArcsecondsPerPixelX;
            if (!double.IsFinite(determinant) || Math.Abs(determinant) < 1e-9) issues.Add("Commissioned pixel-to-mount transform is singular.");
            if (!double.IsFinite(preset.MountTransform.RmsArcseconds) || preset.MountTransform.RmsArcseconds < 0) issues.Add("Mount transform RMS is invalid.");
        }

        RequireSame(issues, "single correction limit", preset.Motion.MaximumSingleCorrectionArcseconds, binding.MaximumSingleCorrectionArcseconds);
        RequireSame(issues, "cumulative correction limit", preset.Motion.MaximumCumulativeCorrectionArcseconds, binding.MaximumCumulativeCorrectionArcseconds);
        RequireSame(issues, "acquisition time limit", preset.Motion.MaximumAcquisitionMinutes, binding.MaximumAcquisitionMinutes);
        if (preset.Motion.MaximumCorrectionAttempts != binding.MaximumCorrectionAttempts) issues.Add("Correction-attempt limit mismatch.");
        if (preset.Motion.MaximumSingleCorrectionArcseconds <= 0 ||
            preset.Motion.MaximumCumulativeCorrectionArcseconds < preset.Motion.MaximumSingleCorrectionArcseconds ||
            preset.Motion.MaximumCorrectionAttempts <= 0 ||
            preset.Motion.MaximumAcquisitionMinutes <= 0)
        {
            issues.Add("Commissioned motion limits are invalid.");
        }

        if (preset.Environment.RequireSafetyMonitor != configuration.Environment.RequireSafetyMonitor ||
            preset.Environment.RequireOpenDomeOrRoof != configuration.Environment.RequireOpenDomeOrRoof ||
            preset.Environment.RequireWeatherData != configuration.Environment.RequireWeatherData)
        {
            issues.Add("Commissioning preset environment interlock requirements mismatch.");
        }
        RequireSame(issues, "maximum cloud cover", preset.Environment.MaximumCloudCoverPercent, configuration.Environment.MaximumCloudCoverPercent);
        RequireSame(issues, "maximum humidity", preset.Environment.MaximumHumidityPercent, configuration.Environment.MaximumHumidityPercent);
        RequireSame(issues, "maximum wind speed", preset.Environment.MaximumWindSpeedMetersPerSecond, configuration.Environment.MaximumWindSpeedMetersPerSecond);
    }

    private static void RequireSame(List<string> issues, string name, double expected, double actual)
    {
        if (!double.IsFinite(expected) || !double.IsFinite(actual) || Math.Abs(expected - actual) > 1e-9)
        {
            issues.Add($"Commissioning preset {name} does not match the Profile binding.");
        }
    }

    private static void ValidateHardwareFingerprint(
        RealRunConfiguration configuration,
        RealCommissioningPreset preset,
        List<string> issues)
    {
        if (preset.HardwareFingerprint is not { } fingerprint)
        {
            issues.Add("Commissioning hardware fingerprint is missing.");
            return;
        }
        if (!string.Equals(fingerprint.AtrCameraStableId, configuration.NightSetup.AtrStableId, StringComparison.Ordinal)) issues.Add("Hardware fingerprint ATR identity mismatch.");
        if (!string.Equals(fingerprint.G3CameraStableId, configuration.Phd2.CameraStableId, StringComparison.OrdinalIgnoreCase)) issues.Add("Hardware fingerprint G3 identity mismatch.");
        if (!string.Equals(fingerprint.QhyCameraStableId, configuration.NightSetup.QhyStableId, StringComparison.Ordinal)) issues.Add("Hardware fingerprint QHY identity mismatch.");
        if (!string.Equals(fingerprint.TelescopeDeviceId, configuration.ExpectedTelescopeId, StringComparison.Ordinal)) issues.Add("Hardware fingerprint telescope identity mismatch.");
        if (!string.Equals(fingerprint.NightSetupId, configuration.NightSetup.NightSetupId, StringComparison.Ordinal)) issues.Add("Hardware fingerprint Night Setup ID mismatch.");
        if (!string.Equals(NormalizeHash(fingerprint.NightSetupSha256), NormalizeHash(configuration.NightSetup.SnapshotSha256), StringComparison.OrdinalIgnoreCase)) issues.Add("Hardware fingerprint Night Setup SHA-256 mismatch.");
        if (!string.Equals(NormalizeHash(fingerprint.Phd2ProfileEvidenceSha256), NormalizeHash(configuration.Phd2.ProfileEvidenceSha256), StringComparison.OrdinalIgnoreCase)) issues.Add("Hardware fingerprint PHD2 evidence SHA-256 mismatch.");

        var canonical = JsonSerializer.Serialize(new
        {
            fingerprint.AtrCameraStableId,
            fingerprint.G3CameraStableId,
            fingerprint.QhyCameraStableId,
            fingerprint.TelescopeDeviceId,
            fingerprint.NightSetupId,
            NightSetupSha256 = NormalizeHash(fingerprint.NightSetupSha256).ToUpperInvariant(),
            Phd2ProfileEvidenceSha256 = NormalizeHash(fingerprint.Phd2ProfileEvidenceSha256).ToUpperInvariant(),
        });
        var computed = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        if (!string.Equals(computed, NormalizeHash(fingerprint.Sha256), StringComparison.OrdinalIgnoreCase)) issues.Add("Commissioning hardware fingerprint self-hash is invalid.");
        if (!string.Equals(computed, NormalizeHash(configuration.Commissioning.HardwareFingerprintSha256), StringComparison.OrdinalIgnoreCase)) issues.Add("Commissioning hardware fingerprint is not the one bound to this locked run.");
    }

    private static string NormalizeHash(string value) => value.Replace("-", string.Empty, StringComparison.Ordinal).Trim();
}
