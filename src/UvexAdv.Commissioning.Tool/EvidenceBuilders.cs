using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UvexAdv.Observatory;
using UvexAdv.Phd2;

namespace UvexAdv.Commissioning.Tool;

public sealed record CommissioningInputFiles(
    string DefinitionPath,
    string DefinitionSha256,
    string NightSetupPath,
    string NightSetupSha256,
    string Phd2EvidencePath,
    string Phd2EvidenceFileSha256,
    string Phd2ProfileEvidenceSha256);

public static class EvidenceBuilders
{
    public static async Task<(Phd2ProfileBindingSnapshot Evidence, WrittenArtifact Artifact, Phd2EvidenceBindings Bindings)> ExportPhd2Async(
        Phd2ProfileBindingRequirement requirement,
        string outputPath,
        bool overwrite,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("PHD2 registry evidence is available only on Windows.");
        if (overwrite) throw new InvalidOperationException("Commissioning evidence is immutable; write a new versioned output path.");
        var validation = WindowsPhd2ProfileEvidence.ReadAndValidate(requirement);
        RequireValid(validation, "Current PHD2 registry profile");
        var evidence = validation.Evidence!;
        var bundle = await ArtifactIO.WriteEvidenceBundleAtomicallyAsync(
            outputPath,
            evidence,
            artifact => new Phd2EvidenceBindings(artifact.AbsolutePath, artifact.Sha256, evidence.Sha256, evidence.CapturedUtc),
            cancellationToken).ConfigureAwait(false);
        return (evidence, bundle.Artifact, bundle.Bindings);
    }

    public static async Task<ValidationSummary> ValidatePhd2Async(
        Phd2ProfileBindingRequirement requirement,
        string evidencePath,
        string expectedFileSha256,
        string expectedProfileEvidenceSha256,
        CancellationToken cancellationToken = default)
    {
        var issues = new List<string>();
        byte[] bytes;
        try { bytes = await ArtifactIO.ReadAndVerifyAsync(evidencePath, expectedFileSha256, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) { return Invalid("phd2-profile", evidencePath, string.Empty, ex.Message); }

        var fileSha = Convert.ToHexString(SHA256.HashData(bytes));
        Phd2ProfileBindingSnapshot? exported = null;
        try { exported = JsonSerializer.Deserialize<Phd2ProfileBindingSnapshot>(bytes, ArtifactIO.JsonOptions); }
        catch (Exception ex) { issues.Add($"PHD2 evidence JSON is invalid: {ex.Message}"); }
        if (exported is null) issues.Add("PHD2 evidence JSON is empty.");
        if (exported is not null)
        {
            var exportedValidation = WindowsPhd2ProfileEvidence.Validate(requirement, exported);
            AddValidationIssues(exportedValidation, "Exported PHD2 evidence", issues);
            if (!SameHash(exported.Sha256, expectedProfileEvidenceSha256)) issues.Add("Exported PHD2 canonical evidence hash does not match the explicit binding hash.");
            if (!SameHash(exported.Sha256, ComputePhd2ProfileEvidenceSha256(exported))) issues.Add("Exported PHD2 evidence self-hash is invalid.");
        }

        if (!OperatingSystem.IsWindows())
        {
            issues.Add("Current PHD2 registry evidence cannot be revalidated on this operating system.");
        }
        else
        {
            var current = WindowsPhd2ProfileEvidence.ReadAndValidate(requirement);
            AddValidationIssues(current, "Current PHD2 registry profile", issues);
            if (exported is not null && current.Evidence is not null && !SameHash(exported.Sha256, current.Evidence.Sha256))
            {
                issues.Add("Current PHD2 registry profile differs from the exported evidence snapshot.");
            }
        }

        return Summary("phd2-profile", evidencePath, fileSha, issues,
            new Dictionary<string, string> { ["Phd2ProfileEvidenceSha256"] = ArtifactIO.NormalizeHash(expectedProfileEvidenceSha256) });
    }

    public static async Task<(NightSetupRecord Setup, WrittenArtifact Artifact, NightSetupBindings Bindings)> CreateNightSetupAsync(
        string definitionPath,
        string definitionSha256,
        string outputPath,
        bool overwrite,
        CancellationToken cancellationToken = default)
    {
        if (overwrite) throw new InvalidOperationException("Commissioning evidence is immutable; write a new versioned output path.");
        var bytes = await ArtifactIO.ReadAndVerifyAsync(definitionPath, definitionSha256, cancellationToken).ConfigureAwait(false);
        RequireNightSetupJsonShape(bytes);
        var setup = DeserializeNightSetup(bytes);
        RequireNoIssues(ValidateNightSetup(setup), "Night Setup definition");
        var bundle = await ArtifactIO.WriteEvidenceBundleAtomicallyAsync(
            outputPath,
            setup,
            artifact => new NightSetupBindings(artifact.AbsolutePath, setup.NightSetupId, artifact.Sha256, setup.LockedUtc),
            cancellationToken).ConfigureAwait(false);
        return (setup, bundle.Artifact, bundle.Bindings);
    }

    public static async Task<ValidationSummary> ValidateNightSetupAsync(
        string nightSetupPath,
        string expectedSha256,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var bytes = await ArtifactIO.ReadAndVerifyAsync(nightSetupPath, expectedSha256, cancellationToken).ConfigureAwait(false);
            RequireNightSetupJsonShape(bytes);
            var setup = DeserializeNightSetup(bytes);
            var issues = ValidateNightSetup(setup);
            var bindings = new Dictionary<string, string>
            {
                ["NightSetupId"] = setup.NightSetupId,
                ["NightSetupSha256"] = ArtifactIO.NormalizeHash(expectedSha256),
                ["NightSetupSchemaVersion"] = setup.SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            };
            if (setup.FocusDomains is not null)
            {
                bindings["FocusDomainRoles"] = string.Join(",", setup.FocusDomains.Select(binding => binding.Role).OrderBy(role => role));
            }
            return Summary("night-setup", nightSetupPath, Convert.ToHexString(SHA256.HashData(bytes)), issues, bindings);
        }
        catch (Exception ex)
        {
            return Invalid("night-setup", nightSetupPath, string.Empty, ex.Message);
        }
    }

    public static async Task<(CommissioningPresetContract Preset, WrittenArtifact Artifact, CommissioningBindings Bindings)> CreateCommissioningPresetAsync(
        CommissioningInputFiles inputs,
        string outputPath,
        bool overwrite,
        bool verifyCurrentPhd2Registry = true,
        CancellationToken cancellationToken = default)
    {
        if (overwrite) throw new InvalidOperationException("Commissioning evidence is immutable; write a new versioned output path.");
        var source = await LoadCommissioningInputsAsync(inputs, verifyCurrentPhd2Registry, cancellationToken).ConfigureAwait(false);
        var definitionPath = Path.GetFullPath(inputs.DefinitionPath);
        var definitionDirectory = Path.GetDirectoryName(definitionPath)!;
        var preset = BuildPreset(source, definitionDirectory);
        var bundle = await ArtifactIO.WriteEvidenceBundleAtomicallyAsync(
            outputPath,
            preset,
            artifact => new CommissioningBindings(
                artifact.AbsolutePath,
                preset.PresetId,
                artifact.Sha256,
                preset.HardwareFingerprint!.Sha256,
                definitionPath,
                ArtifactIO.NormalizeHash(inputs.DefinitionSha256),
                ResolveEvidencePath(source.Definition.Slit.EvidencePath, definitionDirectory),
                ArtifactIO.NormalizeHash(source.Definition.Slit.EvidenceSha256),
                Path.GetFullPath(inputs.NightSetupPath),
                ArtifactIO.NormalizeHash(inputs.NightSetupSha256),
                Path.GetFullPath(inputs.Phd2EvidencePath),
                ArtifactIO.NormalizeHash(inputs.Phd2ProfileEvidenceSha256),
                preset.ValidUntilUtc!.Value,
                CreateNinaProfileValues(preset, artifact.Sha256, source.Definition, source.NightSetup, source.Phd2Evidence, artifact.AbsolutePath, inputs.NightSetupPath)),
            cancellationToken,
            ArtifactIO.CommissioningPresetJsonOptions).ConfigureAwait(false);
        return (preset, bundle.Artifact, bundle.Bindings);
    }

    public static async Task<ValidationSummary> ValidateCommissioningPresetAsync(
        CommissioningInputFiles inputs,
        string presetPath,
        string presetSha256,
        bool verifyCurrentPhd2Registry = true,
        CancellationToken cancellationToken = default)
    {
        var issues = new List<string>();
        byte[] presetBytes;
        try { presetBytes = await ArtifactIO.ReadAndVerifyAsync(presetPath, presetSha256, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) { return Invalid("commissioning-preset", presetPath, string.Empty, ex.Message); }

        CommissioningPresetContract? actual = null;
        try { actual = JsonSerializer.Deserialize<CommissioningPresetContract>(presetBytes, ArtifactIO.JsonOptions); }
        catch (Exception ex) { issues.Add($"Commissioning preset JSON is invalid: {ex.Message}"); }
        if (actual is null) issues.Add("Commissioning preset JSON is empty.");

        LoadedCommissioningInputs? source = null;
        try { source = await LoadCommissioningInputsAsync(inputs, verifyCurrentPhd2Registry, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) { issues.Add(ex.Message); }

        CommissioningPresetContract? expected = null;
        if (source is not null)
        {
            try { expected = BuildPreset(source, Path.GetDirectoryName(Path.GetFullPath(inputs.DefinitionPath))!); }
            catch (Exception ex) { issues.Add(ex.Message); }
        }

        if (actual is not null)
        {
            issues.AddRange(ValidatePresetIntrinsic(actual));
            if (expected is not null)
            {
                var actualCanonical = JsonSerializer.Serialize(actual, ArtifactIO.CanonicalJsonOptions);
                var expectedCanonical = JsonSerializer.Serialize(expected, ArtifactIO.CanonicalJsonOptions);
                if (!string.Equals(actualCanonical, expectedCanonical, StringComparison.Ordinal))
                {
                    issues.Add("Commissioning preset does not exactly reproduce the locked measurement definition and referenced Night Setup/PHD2 evidence.");
                }
            }
        }

        var bindings = new Dictionary<string, string>();
        if (actual is not null)
        {
            bindings["PresetId"] = actual.PresetId;
            bindings["PresetSha256"] = ArtifactIO.NormalizeHash(presetSha256);
            bindings["HardwareFingerprintSha256"] = actual.HardwareFingerprint?.Sha256 ?? string.Empty;
        }
        return Summary("commissioning-preset", presetPath, Convert.ToHexString(SHA256.HashData(presetBytes)), issues, bindings);
    }

    public static string ComputeHardwareFingerprintSha256(HardwareFingerprintContract fingerprint)
    {
        var canonical = JsonSerializer.Serialize(new HardwareFingerprintCanonical(
            fingerprint.AtrCameraStableId,
            fingerprint.G3CameraStableId,
            fingerprint.QhyCameraStableId,
            fingerprint.TelescopeDeviceId,
            fingerprint.NightSetupId,
            ArtifactIO.NormalizeHash(fingerprint.NightSetupSha256),
            ArtifactIO.NormalizeHash(fingerprint.Phd2ProfileEvidenceSha256)), ArtifactIO.CanonicalJsonOptions);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static string ComputePhd2ProfileEvidenceSha256(Phd2ProfileBindingSnapshot evidence)
    {
        var canonical = JsonSerializer.Serialize(new Phd2ProfileEvidenceCanonical(
            evidence.ProfileId,
            evidence.ProfileName,
            evidence.CameraName,
            evidence.CameraStableIds,
            evidence.MountName,
            evidence.Binning,
            evidence.GainPercent,
            evidence.FocalLengthMillimeters,
            evidence.CameraBitsPerPixel), ArtifactIO.CanonicalJsonOptions);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static async Task<LoadedCommissioningInputs> LoadCommissioningInputsAsync(
        CommissioningInputFiles inputs,
        bool verifyCurrentPhd2Registry,
        CancellationToken cancellationToken)
    {
        var definitionBytes = await ArtifactIO.ReadAndVerifyAsync(inputs.DefinitionPath, inputs.DefinitionSha256, cancellationToken).ConfigureAwait(false);
        var nightBytes = await ArtifactIO.ReadAndVerifyAsync(inputs.NightSetupPath, inputs.NightSetupSha256, cancellationToken).ConfigureAwait(false);
        var phdBytes = await ArtifactIO.ReadAndVerifyAsync(inputs.Phd2EvidencePath, inputs.Phd2EvidenceFileSha256, cancellationToken).ConfigureAwait(false);

        RequireCommissioningDefinitionJsonShape(definitionBytes);
        RequireNightSetupJsonShape(nightBytes);
        var definition = JsonSerializer.Deserialize<CommissioningMeasurementDefinition>(definitionBytes, ArtifactIO.JsonOptions)
            ?? throw new InvalidDataException("Commissioning measurement definition JSON is empty.");
        var nightSetup = DeserializeNightSetup(nightBytes);
        var phdEvidence = JsonSerializer.Deserialize<Phd2ProfileBindingSnapshot>(phdBytes, ArtifactIO.JsonOptions)
            ?? throw new InvalidDataException("PHD2 profile evidence JSON is empty.");

        RequireNoIssues(ValidateNightSetup(nightSetup), "Referenced Night Setup");
        if (!SameHash(phdEvidence.Sha256, inputs.Phd2ProfileEvidenceSha256))
        {
            throw new InvalidDataException("PHD2 profile evidence canonical hash does not match the explicit binding hash.");
        }
        if (!SameHash(phdEvidence.Sha256, ComputePhd2ProfileEvidenceSha256(phdEvidence)))
        {
            throw new InvalidDataException("PHD2 profile evidence self-hash is invalid.");
        }
        if (verifyCurrentPhd2Registry)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Current PHD2 registry evidence can only be verified on Windows.");
            var stableId = phdEvidence.CameraStableIds.SingleOrDefault()
                ?? throw new InvalidDataException("PHD2 profile evidence does not contain one unambiguous camera identity.");
            var requirement = new Phd2ProfileBindingRequirement(
                phdEvidence.ProfileId,
                phdEvidence.ProfileName,
                phdEvidence.CameraName,
                stableId,
                phdEvidence.MountName,
                phdEvidence.Binning,
                phdEvidence.GainPercent);
            var current = WindowsPhd2ProfileEvidence.ReadAndValidate(requirement);
            RequireValid(current, "Current PHD2 registry profile");
            if (!SameHash(current.Evidence!.Sha256, phdEvidence.Sha256))
            {
                throw new InvalidDataException("Current PHD2 registry profile differs from the locked evidence file.");
            }
        }
        return new LoadedCommissioningInputs(
            definition,
            nightSetup,
            phdEvidence,
            ArtifactIO.NormalizeHash(inputs.NightSetupSha256),
            ArtifactIO.NormalizeHash(inputs.Phd2ProfileEvidenceSha256));
    }

    private static CommissioningPresetContract BuildPreset(
        LoadedCommissioningInputs source,
        string definitionDirectory)
    {
        var definition = source.Definition;
        var nightSetup = source.NightSetup;
        var phdEvidence = source.Phd2Evidence;
        var issues = ValidateCommissioningDefinition(definition, nightSetup, phdEvidence, definitionDirectory);
        RequireNoIssues(issues, "Commissioning measurement definition");

        var transformResult = MountTransformCalibrator.Fit(
            definition.MountTransform.CalibrationId,
            definition.MountTransform.PierSide,
            definition.MountTransform.Samples,
            definition.MountTransform.MaximumResidualArcseconds,
            definition.MountTransform.MaximumConditionEstimate);
        if (transformResult.Gate.Disposition != GateDisposition.Passed || transformResult.Transform is null)
        {
            throw new InvalidDataException($"Measured mount samples did not pass commissioning: {transformResult.Gate.Message}");
        }

        var fingerprintWithoutHash = new HardwareFingerprintContract(
            nightSetup.Atr585m.StableDeviceId,
            definition.G3CameraStableId,
            nightSetup.QhyMiniCam8m.StableDeviceId,
            definition.TelescopeDeviceId,
            nightSetup.NightSetupId,
            source.NightSetupSha256,
            source.Phd2ProfileEvidenceSha256,
            string.Empty);
        var fingerprint = fingerprintWithoutHash with
        {
            Sha256 = ComputeHardwareFingerprintSha256(fingerprintWithoutHash),
        };
        var transform = transformResult.Transform;
        return new CommissioningPresetContract(
            CommissioningPresetContract.CurrentSchemaVersion,
            definition.PresetId,
            definition.CreatedUtc,
            definition.Provenance,
            nightSetup.NightSetupId,
            source.NightSetupSha256,
            source.Phd2ProfileEvidenceSha256,
            definition.Phd2CalibrationTimestampUtc,
            definition.TelescopeDeviceId,
            definition.G3CameraStableId,
            definition.G3Binning,
            definition.G3ExposureMilliseconds,
            definition.G3GainPercent,
            definition.G3ExpectedWcsFlipped,
            new SlitGeometryContract(
                definition.Slit.CalibrationId,
                definition.Slit.AcquisitionX,
                definition.Slit.AcquisitionY,
                definition.Slit.AngleDegrees,
                definition.Slit.LengthPixels,
                definition.Slit.WidthPixels,
                definition.Slit.UncertaintyPixels),
            new MountTransformContract(
                definition.MountTransform.CalibrationId,
                definition.MountTransform.PierSide,
                transform.RaArcsecondsPerPixelX,
                transform.RaArcsecondsPerPixelY,
                transform.DecArcsecondsPerPixelX,
                transform.DecArcsecondsPerPixelY,
                transform.RmsArcseconds),
            definition.Motion,
            definition.Environment,
            definition.ValidUntilUtc,
            fingerprint,
            definition.G3SaturationAdu,
            definition.FineMotionAuthority,
            definition.Phd2SlitPlacement,
            definition.GhostAssistance,
            BuildSlitWheelIdentity(definition));
    }

    private static SlitWheelIdentityCalibration BuildSlitWheelIdentity(CommissioningMeasurementDefinition definition)
    {
        var measured = definition.SlitWheelIdentity
            ?? throw new InvalidDataException("Four-slot LED slit-width identity measurements are required.");
        var fingerprints = measured.Fingerprints
            .Select(item => new SlitWidthFingerprint(
                item.WheelPosition,
                item.SlitLabel,
                item.NominalWidthMicrometers,
                item.MeasuredWidthPixels,
                item.WidthUncertaintyPixels,
                item.MeasuredUtc,
                ArtifactIO.NormalizeHash(item.EvidenceSha256),
                Enum.IsDefined(typeof(SlitDarkApertureResolution), item.Resolution)
                    ? (SlitDarkApertureResolution)item.Resolution
                    : SlitDarkApertureResolution.Unresolved,
                item.ReflectiveEdgeToApertureCenterPixels,
                item.SecondaryEdgeAmplitudeRatio,
                ArtifactIO.NormalizeHash(item.ShortExposureEvidenceSha256),
                ArtifactIO.NormalizeHash(item.LongExposureEvidenceSha256)))
            .ToArray();
        return new SlitWheelIdentityCalibration(
            SlitWheelIdentityCalibration.CurrentSchemaVersion,
            measured.CalibrationId,
            measured.InstallationEpochId,
            definition.G3CameraStableId,
            definition.G3Binning,
            definition.G3Binning,
            measured.ImageWidthPixels,
            measured.ImageHeightPixels,
            measured.MaximumNormalizedResidual,
            measured.MinimumRunnerUpSeparationSigma,
            fingerprints,
            string.Empty,
            measured.MeasurementModelId,
            measured.ShortExposureMilliseconds,
            measured.LongExposureMilliseconds,
            measured.EdgePsfAlphaPixels,
            measured.EdgePsfBeta).WithComputedSha256();
    }

    private static IReadOnlyList<string> ValidateNightSetup(NightSetupRecord setup)
    {
        var issues = setup.Validate().ToList();
        if (setup.SchemaVersion == NightSetupRecord.LegacySchemaVersion)
        {
            issues.Add("Night Setup schema 1 is readable for migration, but it cannot commission real actions because TelescopeFocusPositionSteps does not bind the three independent focus domains.");
        }
        if (setup.LockedUtc == default) issues.Add("Night Setup LockedUtc is required.");
        if (setup.LockedUtc > DateTimeOffset.UtcNow.AddMinutes(5)) issues.Add("Night Setup LockedUtc is in the future.");
        if (!double.IsFinite(setup.SlitWidthMicrometers) || setup.SlitWidthMicrometers <= 0) issues.Add("Slit width must be finite and positive.");
        if (!double.IsFinite(setup.NominalCentralWavelengthNanometers) || setup.NominalCentralWavelengthNanometers <= 0) issues.Add("Nominal central wavelength must be finite and positive.");
        if (!double.IsFinite(setup.ExpectedMinimumWavelengthNanometers) || setup.ExpectedMinimumWavelengthNanometers <= 0 ||
            !double.IsFinite(setup.ExpectedMaximumWavelengthNanometers)) issues.Add("Expected wavelength range must be finite and positive.");
        if (!Enum.IsDefined(setup.DispersionDirection)) issues.Add("Dispersion direction is invalid.");
        if (!Enum.IsDefined(setup.CalibrationStrategy)) issues.Add("Calibration strategy is invalid.");
        if (string.IsNullOrWhiteSpace(setup.SafetyCapability)) issues.Add("Safety capability is required and must describe measured/available safety evidence.");
        ValidateCamera("ATR585M", setup.Atr585m, issues);
        ValidateCamera("QHYminiCam8M", setup.QhyMiniCam8m, issues);
        if (setup.Atr585m.TemperatureC is null) issues.Add("ATR585M target temperature must be explicit for a real Night Setup.");
        if (setup.QhyMiniCam8m.BinningX != setup.QhyMiniCam8m.BinningY) issues.Add("QHYminiCam8M asymmetric binning is not supported by the real-run binding.");
        if (!short.TryParse(setup.Atr585m.ReadoutMode, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var atrReadout) || atrReadout < 0) issues.Add("ATR585M ReadoutMode must be the explicit non-negative N.I.N.A. readout-mode index.");
        if (!int.TryParse(setup.QhyMiniCam8m.ReadoutMode, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var qhyReadout) || qhyReadout < 0) issues.Add("QHYminiCam8M ReadoutMode must be the explicit non-negative service readout-mode index.");
        if (setup.HorizonPolicy.BaseMinimumAltitudeDegrees is < 0 or >= 90) issues.Add("Horizon base altitude must be in [0, 90) degrees.");
        if (setup.HorizonPolicy.StartMarginDegrees < 0 || setup.HorizonPolicy.ContinueMarginDegrees < 0) issues.Add("Horizon margins cannot be negative.");
        if (setup.HorizonPolicy.EffectiveSampleInterval <= TimeSpan.Zero) issues.Add("Horizon sample interval must be positive.");
        if (setup.SecondOrderRiskOnsetAngstrom is { } onset && (!double.IsFinite(onset) || onset <= 0)) issues.Add("Second-order risk onset must be finite and positive when supplied.");
        if (setup.SchemaVersion == NightSetupRecord.CurrentSchemaVersion && setup.FocusDomains is not null)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var binding in setup.FocusDomains.Where(binding => binding.ValidUntilUtc <= now))
            {
                issues.Add($"Focus domain {binding.Role} evidence expired at {binding.ValidUntilUtc:O}.");
            }
        }
        return issues;
    }

    private static void ValidateCamera(string label, CameraSetup camera, List<string> issues)
    {
        if (camera is null) { issues.Add($"{label} setup is required."); return; }
        if (string.IsNullOrWhiteSpace(camera.StableDeviceId)) issues.Add($"{label} stable identity is required.");
        if (camera.BinningX <= 0 || camera.BinningY <= 0) issues.Add($"{label} binning must be positive.");
        if (string.IsNullOrWhiteSpace(camera.ReadoutMode)) issues.Add($"{label} readout mode is required.");
        if (camera.RoiX < 0 || camera.RoiY < 0 || camera.RoiWidth <= 0 || camera.RoiHeight <= 0) issues.Add($"{label} ROI is invalid.");
        if (camera.TemperatureC is { } temperature && !double.IsFinite(temperature)) issues.Add($"{label} temperature must be finite or null.");
    }

    private static IReadOnlyList<string> ValidateCommissioningDefinition(
        CommissioningMeasurementDefinition definition,
        NightSetupRecord nightSetup,
        Phd2ProfileBindingSnapshot phdEvidence,
        string definitionDirectory)
    {
        var issues = new List<string>();
        if (definition.SchemaVersion != CommissioningMeasurementDefinition.CurrentSchemaVersion)
            issues.Add($"Commissioning measurement definition schema must be {CommissioningMeasurementDefinition.CurrentSchemaVersion}.");
        if (string.IsNullOrWhiteSpace(definition.PresetId)) issues.Add("PresetId is required.");
        if (definition.CreatedUtc == default || definition.CreatedUtc > DateTimeOffset.UtcNow.AddMinutes(5)) issues.Add("CreatedUtc is missing or in the future.");
        if (definition.ValidUntilUtc <= definition.CreatedUtc || definition.ValidUntilUtc <= DateTimeOffset.UtcNow) issues.Add("ValidUntilUtc must be after CreatedUtc and in the future.");
        if (string.IsNullOrWhiteSpace(definition.Provenance)) issues.Add("Measurement provenance is required.");
        if (!DateTimeOffset.TryParse(definition.Phd2CalibrationTimestampUtc, out var calibrationUtc) || calibrationUtc.Offset != TimeSpan.Zero || calibrationUtc > DateTimeOffset.UtcNow.AddMinutes(5)) issues.Add("PHD2 calibration timestamp is missing, not UTC, invalid, or in the future.");
        if (string.IsNullOrWhiteSpace(definition.TelescopeDeviceId)) issues.Add("Exact telescope device identity is required.");
        if (!string.Equals(definition.G3CameraStableId, nightSetup.G3StableDeviceId, StringComparison.OrdinalIgnoreCase)) issues.Add("G3 identity does not match Night Setup.");
        if (!string.Equals(definition.G3CameraStableId, phdEvidence.CameraStableIds.SingleOrDefault(), StringComparison.OrdinalIgnoreCase)) issues.Add("G3 identity does not match the unambiguous PHD2 registry evidence.");
        if (!string.Equals(nightSetup.Phd2ProfileName, phdEvidence.ProfileName, StringComparison.Ordinal)) issues.Add("PHD2 profile name does not match Night Setup.");
        if (definition.G3Binning <= 0 || definition.G3Binning != phdEvidence.Binning) issues.Add("G3 binning is invalid or does not match PHD2 evidence.");
        if (definition.G3GainPercent is < 0 or > 100 || definition.G3GainPercent != phdEvidence.GainPercent) issues.Add("G3 gain is invalid or does not match PHD2 evidence.");
        if (definition.G3ExposureMilliseconds <= 0) issues.Add("Measured G3 acquisition exposure must be positive.");
        if (definition.G3SaturationAdu is <= 0 or > ushort.MaxValue) issues.Add("G3 saturation ADU must fit the unsigned 16-bit FITS container.");
        if (definition.G3SaturationAdu != nightSetup.G3SaturationAdu) issues.Add("G3 saturation ADU does not match Night Setup.");
        if (nightSetup.FocusDomains is null || nightSetup.FocusDomains.Count != 3)
        {
            issues.Add("Commissioning requires three focus-domain bindings from Night Setup schema 2.");
        }
        else
        {
            foreach (var focus in nightSetup.FocusDomains.Where(focus => focus.ValidUntilUtc < definition.ValidUntilUtc))
            {
                issues.Add($"Commissioning validity cannot extend past {focus.Role} focus evidence expiry {focus.ValidUntilUtc:O}.");
            }
        }

        if (!Enum.IsDefined(typeof(CommissioningFineMotionAuthority), definition.FineMotionAuthority))
        {
            issues.Add("FineMotionAuthority must be an explicit numeric production authority (0, 1, or 2).");
        }

        var phd2Placement = definition.Phd2SlitPlacement;
        if (phd2Placement is null)
        {
            issues.Add("Schema-4 commissioning requires an explicit complete Phd2SlitPlacement record.");
        }
        else
        {
            issues.AddRange(phd2Placement.Validate());
            if (phd2Placement.ExpectedGuidingExposureMilliseconds <= 0)
                issues.Add("PHD2 ExpectedGuidingExposureMilliseconds must be explicitly commissioned even when automatic guide-mode selection is used.");
            if (phd2Placement.OffSlitGuidingExposureMilliseconds is not > 0)
                issues.Add("PHD2 ordinary off-slit guiding exposure must be explicitly commissioned.");
            if (phd2Placement.DirectTargetGuidingExposureMilliseconds is not > 0)
                issues.Add("PHD2 shortest direct-target guiding exposure must be explicitly commissioned separately.");
            if (phd2Placement.PierSide is not "East" and not "West")
                issues.Add("PHD2 pier side must be the exact production value 'East' or 'West'.");
            if (definition.MountTransform is not null &&
                !string.Equals(phd2Placement.PierSide, definition.MountTransform.PierSide, StringComparison.Ordinal))
                issues.Add("PHD2 slit-placement pier side must exactly match the independently measured mount-transform pier side.");
            try
            {
                var computedTopology = phd2Placement.ComputeTopologyFingerprintSha256(
                    phdEvidence,
                    definition.G3CameraStableId,
                    definition.G3Binning);
                if (!SameHash(computedTopology, phd2Placement.LockedTopologyFingerprintSha256))
                    issues.Add("PHD2 locked topology fingerprint does not match the explicit profile/runtime identity, installation epoch, sensor, ROI, binning, rotation and pier-side fields.");
            }
            catch (Exception ex)
            {
                issues.Add($"PHD2 locked topology could not be computed: {ex.Message}");
            }
        }
        ValidateGhostDefinition(definition, nightSetup, phdEvidence, issues);

        var slit = definition.Slit;
        if (slit is null) issues.Add("Measured slit geometry is required.");
        else
        {
            if (string.IsNullOrWhiteSpace(slit.CalibrationId)) issues.Add("Slit calibration ID is required.");
            if (slit.MeasuredUtc == default || slit.MeasuredUtc > definition.CreatedUtc.AddMinutes(5)) issues.Add("Slit measurement timestamp is missing or after preset creation.");
            if (!double.IsFinite(slit.AcquisitionX) || !double.IsFinite(slit.AcquisitionY) || !double.IsFinite(slit.AngleDegrees) ||
                !double.IsFinite(slit.LengthPixels) || !double.IsFinite(slit.WidthPixels) || !double.IsFinite(slit.UncertaintyPixels) ||
                slit.LengthPixels <= 0 || slit.WidthPixels <= 0 || slit.UncertaintyPixels < 0) issues.Add("Measured slit geometry contains invalid values.");
            try
            {
                var evidencePath = ResolveEvidencePath(slit.EvidencePath, definitionDirectory);
                var expected = ArtifactIO.NormalizeHash(slit.EvidenceSha256);
                if (!ArtifactIO.IsSha256(expected)) issues.Add("Slit measurement evidence requires an explicit SHA-256.");
                else if (!File.Exists(evidencePath)) issues.Add($"Slit measurement evidence does not exist: {evidencePath}");
                else if (!SameHash(ArtifactIO.ComputeFileSha256(evidencePath), expected)) issues.Add("Slit measurement evidence SHA-256 mismatch.");
            }
            catch (Exception ex) { issues.Add($"Slit measurement evidence is invalid: {ex.Message}"); }
        }

        ValidateSlitWheelIdentityDefinition(definition, nightSetup, definitionDirectory, issues);

        var mount = definition.MountTransform;
        if (mount is null) issues.Add("Measured mount transform samples are required.");
        else
        {
            if (string.IsNullOrWhiteSpace(mount.CalibrationId) || string.IsNullOrWhiteSpace(mount.PierSide)) issues.Add("Mount calibration ID and pier side are required.");
            if (mount.MeasuredUtc == default || mount.MeasuredUtc > definition.CreatedUtc.AddMinutes(5)) issues.Add("Mount measurement timestamp is missing or after preset creation.");
            if (mount.Samples is null || mount.Samples.Count < 4) issues.Add("At least four explicitly measured mount samples are required; seed values cannot commission real mode.");
            if (!double.IsFinite(mount.MaximumResidualArcseconds) || mount.MaximumResidualArcseconds <= 0 ||
                !double.IsFinite(mount.MaximumConditionEstimate) || mount.MaximumConditionEstimate <= 1 ||
                !double.IsFinite(mount.MaximumSampleMotionArcseconds) || mount.MaximumSampleMotionArcseconds <= 0) issues.Add("Mount fit limits must be finite and positive.");
            if (mount.Samples is not null)
            {
                foreach (var sample in mount.Samples)
                {
                    var values = new[] { sample.CommandedRaArcseconds, sample.CommandedDecArcseconds, sample.MeasuredPixelShiftX, sample.MeasuredPixelShiftY };
                    if (values.Any(value => !double.IsFinite(value))) { issues.Add("Mount samples must contain only finite values."); break; }
                    var command = Math.Sqrt(sample.CommandedRaArcseconds * sample.CommandedRaArcseconds + sample.CommandedDecArcseconds * sample.CommandedDecArcseconds);
                    var shift = Math.Sqrt(sample.MeasuredPixelShiftX * sample.MeasuredPixelShiftX + sample.MeasuredPixelShiftY * sample.MeasuredPixelShiftY);
                    if (command <= 0 || shift <= 0 || command > mount.MaximumSampleMotionArcseconds + 1e-9) { issues.Add("Every mount sample must be a non-zero bounded measured motion."); break; }
                }
            }
        }

        if (definition.Motion is null || definition.Motion.MaximumSingleCorrectionArcseconds <= 0 ||
            definition.Motion.MaximumCumulativeCorrectionArcseconds < definition.Motion.MaximumSingleCorrectionArcseconds ||
            definition.Motion.MaximumCorrectionAttempts <= 0 || definition.Motion.MaximumAcquisitionMinutes <= 0) issues.Add("Motion safety limits are invalid.");
        if (definition.Environment is null ||
            !FinitePercent(definition.Environment.MaximumCloudCoverPercent) ||
            !FinitePercent(definition.Environment.MaximumHumidityPercent) ||
            !double.IsFinite(definition.Environment.MaximumWindSpeedMetersPerSecond) || definition.Environment.MaximumWindSpeedMetersPerSecond < 0) issues.Add("Environment limits are invalid.");
        return issues;
    }

    private static void ValidateSlitWheelIdentityDefinition(
        CommissioningMeasurementDefinition definition,
        NightSetupRecord nightSetup,
        string definitionDirectory,
        List<string> issues)
    {
        var measured = definition.SlitWheelIdentity;
        if (measured is null)
        {
            issues.Add("Schema-4 commissioning requires LED width evidence for all four physical slit-wheel positions.");
            return;
        }

        if (definition.Phd2SlitPlacement is { } placement &&
            !string.Equals(measured.InstallationEpochId, placement.InstallationEpochId, StringComparison.Ordinal))
            issues.Add("Slit-wheel identity installation epoch must match the PHD2/G3 optical-topology installation epoch.");

        if (measured.Fingerprints is null)
        {
            issues.Add("Slit-wheel identity fingerprint list is missing.");
            return;
        }

        foreach (var fingerprint in measured.Fingerprints)
        {
            try
            {
                var evidencePath = ResolveEvidencePath(fingerprint.EvidencePath, definitionDirectory);
                var expected = ArtifactIO.NormalizeHash(fingerprint.EvidenceSha256);
                if (!ArtifactIO.IsSha256(expected))
                    issues.Add($"Slit position {fingerprint.WheelPosition} identity evidence requires an explicit SHA-256.");
                else if (!File.Exists(evidencePath))
                    issues.Add($"Slit position {fingerprint.WheelPosition} identity evidence does not exist: {evidencePath}");
                else if (!SameHash(ArtifactIO.ComputeFileSha256(evidencePath), expected))
                    issues.Add($"Slit position {fingerprint.WheelPosition} identity evidence SHA-256 mismatch.");
            }
            catch (Exception ex)
            {
                issues.Add($"Slit position {fingerprint.WheelPosition} identity evidence is invalid: {ex.Message}");
            }

            ValidateSlitHdrEvidence(
                fingerprint.WheelPosition,
                "short",
                fingerprint.ShortExposureEvidencePath,
                fingerprint.ShortExposureEvidenceSha256,
                definitionDirectory,
                issues);
            ValidateSlitHdrEvidence(
                fingerprint.WheelPosition,
                "long",
                fingerprint.LongExposureEvidencePath,
                fingerprint.LongExposureEvidenceSha256,
                definitionDirectory,
                issues);
        }

        try
        {
            var calibration = BuildSlitWheelIdentity(definition);
            issues.AddRange(calibration.Validate());
            foreach (var expected in UvexSlitWheelLayout.DeclaredSlots)
            {
                var actual = calibration.Fingerprints.FirstOrDefault(item => item.WheelPosition == expected.WheelPosition);
                if (actual is null || Math.Abs(actual.NominalWidthMicrometers - expected.NominalWidthMicrometers) > 0.01)
                    issues.Add($"Slit-wheel position {expected.WheelPosition} must be explicitly identified as {expected.NominalWidthMicrometers:F0} µm; labels are not inferred from pixel-width ratios.");
            }

            var active = calibration.Fingerprints.FirstOrDefault(item => item.WheelPosition == nightSetup.SlitPosition);
            if (active is null || Math.Abs(active.NominalWidthMicrometers - nightSetup.SlitWidthMicrometers) > 0.01)
                issues.Add("The locked Night Setup slit position/width is absent from the four-slot optical identity library.");
        }
        catch (Exception ex)
        {
            issues.Add($"Slit-wheel identity calibration could not be constructed: {ex.Message}");
        }
    }

    private static void ValidateSlitHdrEvidence(
        int wheelPosition,
        string role,
        string path,
        string sha256,
        string definitionDirectory,
        List<string> issues)
    {
        try
        {
            var evidencePath = ResolveEvidencePath(path, definitionDirectory);
            var expected = ArtifactIO.NormalizeHash(sha256);
            if (!ArtifactIO.IsSha256(expected))
                issues.Add($"Slit position {wheelPosition} {role} HDR evidence requires an explicit SHA-256.");
            else if (!File.Exists(evidencePath))
                issues.Add($"Slit position {wheelPosition} {role} HDR evidence does not exist: {evidencePath}");
            else if (!SameHash(ArtifactIO.ComputeFileSha256(evidencePath), expected))
                issues.Add($"Slit position {wheelPosition} {role} HDR evidence SHA-256 mismatch.");
        }
        catch (Exception ex)
        {
            issues.Add($"Slit position {wheelPosition} {role} HDR evidence is invalid: {ex.Message}");
        }
    }

    private static void ValidateGhostDefinition(
        CommissioningMeasurementDefinition definition,
        NightSetupRecord nightSetup,
        Phd2ProfileBindingSnapshot phdEvidence,
        List<string> issues)
    {
        if (!Enum.IsDefined(typeof(GhostAssistanceMode), definition.GhostAssistanceMode))
        {
            issues.Add("GhostAssistanceMode must be the explicit numeric value 0 (Skip), 1 (Auto), or 2 (RequireValid).");
            return;
        }

        var mode = (GhostAssistanceMode)definition.GhostAssistanceMode;
        var ghost = definition.GhostAssistance;
        if (mode == GhostAssistanceMode.Skip && ghost is not null)
            issues.Add("GhostAssistanceMode Skip requires GhostAssistance to be explicitly null; calibrated payloads must select Auto or RequireValid.");
        if (mode != GhostAssistanceMode.Skip && ghost is null)
            issues.Add("GhostAssistanceMode Auto or RequireValid requires a complete hash-bound GhostAssistance payload.");
        if (ghost is null) return;

        issues.AddRange(ghost.Validate());
        var calibration = ghost.Calibration;
        var policy = ghost.MatchPolicy;
        var extraction = ghost.ExtractionPolicy;
        var runtime = ghost.RuntimeFingerprint;
        var phd2Placement = definition.Phd2SlitPlacement;
        if (calibration is null || policy is null || extraction is null || runtime is null || phd2Placement is null)
            return;

        if (!string.Equals(calibration.InstallationEpochId, runtime.InstallationEpochId, StringComparison.Ordinal) ||
            !string.Equals(calibration.InstallationEpochId, phd2Placement.InstallationEpochId, StringComparison.Ordinal))
            issues.Add("Ghost calibration, runtime fingerprint and PHD2 topology must share one exact installation epoch.");
        if (!SameHash(calibration.OpticalTopologySha256, runtime.OpticalTopologySha256))
            issues.Add("Ghost runtime optical-topology SHA-256 does not match the calibration.");
        if (!SameHash(calibration.OrientationFingerprintSha256, runtime.OrientationFingerprintSha256))
            issues.Add("Ghost runtime orientation fingerprint does not match the calibration.");
        if (AngleDifferenceDegrees(calibration.OrientationDegrees, runtime.OrientationDegrees) > policy.MaximumOrientationDifferenceDegrees)
            issues.Add("Ghost runtime orientation angle is outside the explicitly commissioned match-policy tolerance.");
        if (!string.Equals(calibration.CameraStableId, definition.G3CameraStableId, StringComparison.OrdinalIgnoreCase))
            issues.Add("Ghost calibration G3 camera identity does not match the commissioning definition.");
        if (!string.Equals(
                calibration.Phd2ProfileId,
                phdEvidence.ProfileId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                StringComparison.Ordinal))
            issues.Add("Ghost calibration PHD2 profile ID does not match the locked registry evidence.");
        if (!string.Equals(calibration.PierSide, phd2Placement.PierSide, StringComparison.Ordinal))
            issues.Add("Ghost calibration pier side does not match the PHD2 fine-motion topology.");
        if (calibration.Gain != definition.G3GainPercent)
            issues.Add("Ghost calibration gain does not match the commissioned G3 gain.");
        if (definition.G3ExposureMilliseconds < calibration.MinimumExposureMilliseconds ||
            definition.G3ExposureMilliseconds > calibration.MaximumExposureMilliseconds)
            issues.Add("Commissioned G3 exposure is outside the ghost calibration exposure envelope.");
        var expectedDetector = new GhostDetectorGeometry(
            0,
            0,
            phd2Placement.RoiWidth,
            phd2Placement.RoiHeight,
            definition.G3Binning,
            definition.G3Binning);
        if (calibration.Detector != expectedDetector)
            issues.Add("Ghost calibration detector geometry does not match the PHD2 delivered-frame ROI and commissioned G3 binning.");
        if (calibration.CreatedUtc > definition.CreatedUtc.AddMinutes(5) ||
            calibration.ValidUntilUtc < definition.ValidUntilUtc ||
            definition.ValidUntilUtc - calibration.CreatedUtc > policy.MaximumCalibrationAge)
            issues.Add("Ghost calibration time/expiry does not cover the complete commissioning-preset validity interval under its match policy.");

        var c11Focus = nightSetup.FocusDomains?
            .Where(binding => binding.Role == FocusDomainRole.C11Main)
            .ToArray() ?? [];
        if (c11Focus.Length != 1 || c11Focus[0].Confidence < ghost.MinimumC11FocusConfidence)
            issues.Add("Locked Night Setup C11/G3 focus evidence does not meet the ghost-assistance confidence gate.");
    }

    private static double AngleDifferenceDegrees(double left, double right)
    {
        if (!double.IsFinite(left) || !double.IsFinite(right)) return double.PositiveInfinity;
        var difference = Math.Abs(Math.IEEERemainder(left - right, 360));
        return difference > 180 ? 360 - difference : difference;
    }

    private static IReadOnlyList<string> ValidatePresetIntrinsic(CommissioningPresetContract preset)
    {
        var issues = new List<string>();
        if (preset.SchemaVersion != CommissioningPresetContract.CurrentSchemaVersion)
            issues.Add($"Commissioning preset schema must be {CommissioningPresetContract.CurrentSchemaVersion}.");
        if (!Enum.IsDefined(typeof(CommissioningFineMotionAuthority), preset.FineMotionAuthority))
            issues.Add("Commissioning fine-motion authority is invalid.");
        if (preset.Phd2SlitPlacement is null)
            issues.Add("Schema-4 commissioning requires a complete PHD2 slit-placement record.");
        else
            issues.AddRange(preset.Phd2SlitPlacement.Validate());
        if (preset.GhostAssistance is not null)
            issues.AddRange(preset.GhostAssistance.Validate());
        if (preset.SlitWheelIdentity is null)
            issues.Add("Schema-4 commissioning requires a complete four-slot LED slit-width identity calibration.");
        else
            issues.AddRange(preset.SlitWheelIdentity.Validate());
        if (preset.CreatedUtc == default || preset.CreatedUtc > DateTimeOffset.UtcNow.AddMinutes(5)) issues.Add("Preset creation timestamp is invalid.");
        if (preset.ValidUntilUtc is not { } validUntil || validUntil <= preset.CreatedUtc || validUntil <= DateTimeOffset.UtcNow) issues.Add("Preset validity deadline is missing or expired.");
        if (preset.HardwareFingerprint is null) issues.Add("Hardware fingerprint is missing.");
        else
        {
            var computed = ComputeHardwareFingerprintSha256(preset.HardwareFingerprint);
            if (!SameHash(computed, preset.HardwareFingerprint.Sha256)) issues.Add("Hardware fingerprint self-hash is invalid.");
            if (!SameHash(preset.HardwareFingerprint.NightSetupSha256, preset.NightSetupSha256)) issues.Add("Hardware fingerprint Night Setup hash mismatch.");
            if (!SameHash(preset.HardwareFingerprint.Phd2ProfileEvidenceSha256, preset.Phd2ProfileEvidenceSha256)) issues.Add("Hardware fingerprint PHD2 evidence hash mismatch.");
        }
        if (preset.Slit is null) issues.Add("Commissioned slit geometry is missing.");
        if (preset.G3SaturationAdu is <= 0 or > ushort.MaxValue) issues.Add("Commissioned G3 saturation ADU is invalid.");
        if (preset.Motion is null) issues.Add("Commissioned motion limits are missing.");
        if (preset.Environment is null) issues.Add("Commissioned environment limits are missing.");
        if (preset.MountTransform is null)
        {
            issues.Add("Commissioned pixel-to-mount transform is missing.");
        }
        else
        {
            var determinant = preset.MountTransform.RaArcsecondsPerPixelX * preset.MountTransform.DecArcsecondsPerPixelY -
                preset.MountTransform.RaArcsecondsPerPixelY * preset.MountTransform.DecArcsecondsPerPixelX;
            if (!double.IsFinite(determinant) || Math.Abs(determinant) < 1e-9) issues.Add("Commissioned pixel-to-mount transform is singular.");
            if (!double.IsFinite(preset.MountTransform.RmsArcseconds) || preset.MountTransform.RmsArcseconds < 0) issues.Add("Mount transform RMS is invalid.");
        }
        return issues;
    }

    private static NightSetupRecord DeserializeNightSetup(byte[] bytes) =>
        JsonSerializer.Deserialize<NightSetupRecord>(bytes, ArtifactIO.JsonOptions)
        ?? throw new InvalidDataException("Night Setup JSON is empty.");

    private static IReadOnlyDictionary<string, object?> CreateNinaProfileValues(
        CommissioningPresetContract preset,
        string presetSha256,
        CommissioningMeasurementDefinition definition,
        NightSetupRecord nightSetup,
        Phd2ProfileBindingSnapshot phdEvidence,
        string presetPath,
        string nightSetupPath)
    {
        var atrReadout = int.Parse(nightSetup.Atr585m.ReadoutMode, System.Globalization.CultureInfo.InvariantCulture);
        var qhyReadout = int.Parse(nightSetup.QhyMiniCam8m.ReadoutMode, System.Globalization.CultureInfo.InvariantCulture);
        var values = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            // The current loader does not bind every action-bearing Profile
            // hint (notably focal lengths and UVEX tolerance) into schema 4.
            // Evidence generation must therefore never authorize real mode.
            ["ObservationUseRealMode"] = false,
            ["RealModeCommissioned"] = false,
            ["AllowDegradedSupervisedScience"] = false,
            ["GhostAssistanceMode"] = definition.GhostAssistanceMode,
            // Fast paired WCS collection is an optional, zero-mount-motion
            // commissioning aid.  The generated Profile exposes a complete,
            // deterministic policy but leaves it disabled until an operator
            // deliberately enables it for a hash-bound run configuration.
            ["QhyG3FastPairEnabled"] = false,
            ["QhyG3FastPairSchemaVersion"] = QhyG3FastPairPolicy.CurrentSchemaVersion,
            ["QhyG3FastPairPolicyId"] = "qhy-g3-fast-pair-v1",
            ["QhyG3FastPairExposureSeconds"] = 2d,
            ["QhyG3FastPairMaximumCachedAgeSeconds"] = 15d,
            ["QhyG3FastPairMaximumMidpointSeparationSeconds"] = 20d,
            ["QhyG3FastPairMaximumWallClockSeconds"] = 30d,
            ["QhyG3FastPairMaximumMountSpanArcseconds"] = 2d,
            ["QhyG3FastPairCandidateValidityHours"] = 24d,
            ["QhyG3FastPairMaximumCandidateUncertaintyArcseconds"] = 20d,
            ["CommissioningPresetPath"] = Path.GetFullPath(presetPath),
            ["CommissioningPresetId"] = preset.PresetId,
            ["CommissioningPresetSha256"] = ArtifactIO.NormalizeHash(presetSha256),
            ["CommissioningHardwareFingerprintSha256"] = preset.HardwareFingerprint!.Sha256,
            ["NightSetupSnapshotPath"] = Path.GetFullPath(nightSetupPath),
            ["NightSetupSnapshotSha256"] = preset.NightSetupSha256,
            ["ObservationNightSetupId"] = nightSetup.NightSetupId,
            ["ObservationExpectedAtrCameraId"] = nightSetup.Atr585m.StableDeviceId,
            ["ObservationExpectedG3ProfileName"] = nightSetup.Phd2ProfileName,
            ["ObservationExpectedQhyCameraId"] = nightSetup.QhyMiniCam8m.StableDeviceId,
            ["ExpectedTelescopeId"] = definition.TelescopeDeviceId,
            ["Gain"] = nightSetup.Atr585m.Gain,
            ["Offset"] = nightSetup.Atr585m.Offset,
            ["Binning"] = nightSetup.Atr585m.BinningX,
            ["AtrTargetTemperatureC"] = nightSetup.Atr585m.TemperatureC,
            ["AtrReadoutModeIndex"] = atrReadout,
            ["RoiX"] = nightSetup.Atr585m.RoiX,
            ["RoiY"] = nightSetup.Atr585m.RoiY,
            ["RoiWidth"] = nightSetup.Atr585m.RoiWidth,
            ["RoiHeight"] = nightSetup.Atr585m.RoiHeight,
            ["QhyGain"] = nightSetup.QhyMiniCam8m.Gain,
            ["QhyOffset"] = nightSetup.QhyMiniCam8m.Offset,
            ["QhyBinning"] = nightSetup.QhyMiniCam8m.BinningX,
            ["QhyReadoutMode"] = qhyReadout,
            ["QhyRoiX"] = nightSetup.QhyMiniCam8m.RoiX,
            ["QhyRoiY"] = nightSetup.QhyMiniCam8m.RoiY,
            ["QhyRoiWidth"] = nightSetup.QhyMiniCam8m.RoiWidth,
            ["QhyRoiHeight"] = nightSetup.QhyMiniCam8m.RoiHeight,
            ["QhyTargetTemperatureC"] = nightSetup.QhyMiniCam8m.TemperatureC,
            ["Phd2ProfileId"] = phdEvidence.ProfileId,
            ["Phd2ProfileName"] = phdEvidence.ProfileName,
            ["Phd2CameraName"] = phdEvidence.CameraName,
            ["Phd2CameraStableId"] = definition.G3CameraStableId,
            ["Phd2MountName"] = phdEvidence.MountName,
            ["Phd2RuntimeCameraName"] = Phd2RuntimeEquipmentConventions.G3CameraName,
            ["Phd2RuntimeMountName"] = Phd2RuntimeEquipmentConventions.OnStepMountName,
            ["Phd2CalibrationTimestampUtc"] = definition.Phd2CalibrationTimestampUtc,
            ["Phd2ProfileEvidenceSha256"] = preset.Phd2ProfileEvidenceSha256,
            ["G3ExposureMilliseconds"] = definition.G3ExposureMilliseconds,
            ["G3GainPercent"] = definition.G3GainPercent,
            ["G3Binning"] = definition.G3Binning,
            ["G3SaturationAdu"] = definition.G3SaturationAdu,
            ["G3ExpectedWcsFlipped"] = definition.G3ExpectedWcsFlipped,
            ["SlitGeometryCommissioned"] = true,
            ["SlitGeometryCalibrationId"] = preset.Slit.CalibrationId,
            ["SlitSeedX"] = preset.Slit.AcquisitionX,
            ["SlitSeedY"] = preset.Slit.AcquisitionY,
            ["SlitAngleDegrees"] = preset.Slit.AngleDegrees,
            ["SlitLengthPixels"] = preset.Slit.LengthPixels,
            ["SlitWidthPixels"] = preset.Slit.WidthPixels,
            ["SlitUncertaintyPixels"] = preset.Slit.UncertaintyPixels,
            ["MountTransformCommissioned"] = true,
            ["MountTransformCalibrationId"] = preset.MountTransform.CalibrationId,
            ["MountTransformPierSide"] = preset.MountTransform.PierSide,
            ["MountRaArcsecondsPerPixelX"] = preset.MountTransform.RaArcsecondsPerPixelX,
            ["MountRaArcsecondsPerPixelY"] = preset.MountTransform.RaArcsecondsPerPixelY,
            ["MountDecArcsecondsPerPixelX"] = preset.MountTransform.DecArcsecondsPerPixelX,
            ["MountDecArcsecondsPerPixelY"] = preset.MountTransform.DecArcsecondsPerPixelY,
            ["MountTransformRmsArcseconds"] = preset.MountTransform.RmsArcseconds,
            ["MaximumSingleCorrectionArcseconds"] = preset.Motion.MaximumSingleCorrectionArcseconds,
            ["MaximumCumulativeCorrectionArcseconds"] = preset.Motion.MaximumCumulativeCorrectionArcseconds,
            ["MaximumCorrectionAttempts"] = preset.Motion.MaximumCorrectionAttempts,
            ["MaximumAcquisitionMinutes"] = preset.Motion.MaximumAcquisitionMinutes,
            ["ExpectedUvexSlitPosition"] = nightSetup.SlitPosition,
            ["ExpectedUvexGratingPositionSteps"] = nightSetup.GratingPositionSteps,
            ["ExpectedUvexM2PositionSteps"] = nightSetup.M2PositionSteps,
            ["HorizonMinimumDegrees"] = nightSetup.HorizonPolicy.BaseMinimumAltitudeDegrees,
            ["HorizonStartMarginDegrees"] = nightSetup.HorizonPolicy.StartMarginDegrees,
            ["HorizonContinueMarginDegrees"] = nightSetup.HorizonPolicy.ContinueMarginDegrees,
            ["RequireSafetyMonitor"] = preset.Environment.RequireSafetyMonitor,
            ["RequireOpenDomeOrRoof"] = preset.Environment.RequireOpenDomeOrRoof,
            ["RequireWeatherData"] = preset.Environment.RequireWeatherData,
            ["MaximumCloudCoverPercent"] = preset.Environment.MaximumCloudCoverPercent,
            ["MaximumHumidityPercent"] = preset.Environment.MaximumHumidityPercent,
            ["MaximumWindSpeedMetersPerSecond"] = preset.Environment.MaximumWindSpeedMetersPerSecond,
        };
        return values;
    }

    private static void RequireNightSetupJsonShape(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        RequireProperties(root, "Night Setup", [
            "SchemaVersion", "NightSetupId", "LockedUtc", "SlitPosition", "SlitWidthMicrometers",
            "GratingPositionSteps", "NominalCentralWavelengthNanometers", "M2PositionSteps",
            "TelescopeFocusPositionSteps", "Atr585m", "G3StableDeviceId", "Phd2ProfileName",
            "QhyMiniCam8m", "DispersionDirection", "ExpectedMinimumWavelengthNanometers",
            "ExpectedMaximumWavelengthNanometers", "CalibrationStrategy", "CalibrationReference",
            "HorizonPolicy", "SafetyCapability", "LongWavelengthOrderSortingFilterInstalled",
            "SecondOrderRiskOnsetAngstrom"
        ]);
        RequireProperties(GetProperty(root, "Atr585m"), "Night Setup Atr585m", [
            "StableDeviceId", "Gain", "Offset", "BinningX", "BinningY", "TemperatureC", "ReadoutMode",
            "RoiX", "RoiY", "RoiWidth", "RoiHeight"
        ]);
        RequireProperties(GetProperty(root, "QhyMiniCam8m"), "Night Setup QhyMiniCam8m", [
            "StableDeviceId", "Gain", "Offset", "BinningX", "BinningY", "TemperatureC", "ReadoutMode",
            "RoiX", "RoiY", "RoiWidth", "RoiHeight"
        ]);
        RequireProperties(GetProperty(root, "HorizonPolicy"), "Night Setup HorizonPolicy", [
            "BaseMinimumAltitudeDegrees", "StartMarginDegrees", "ContinueMarginDegrees", "SampleInterval", "AzimuthProfile"
        ]);
        var schemaVersionElement = GetProperty(root, "SchemaVersion");
        if (schemaVersionElement.ValueKind != JsonValueKind.Number || !schemaVersionElement.TryGetInt32(out var schemaVersion))
        {
            throw new InvalidDataException("Night Setup SchemaVersion must be an integer.");
        }
        if (schemaVersion != NightSetupRecord.CurrentSchemaVersion) return;

        RequireProperties(root, "Night Setup schema 2", ["FocusDomains", "G3SaturationAdu"]);
        var focusDomains = GetProperty(root, "FocusDomains");
        if (focusDomains.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Night Setup FocusDomains must be an array.");
        var index = 0;
        foreach (var focus in focusDomains.EnumerateArray())
        {
            var label = $"Night Setup FocusDomains[{index}]";
            RequireProperties(focus, label, [
                "Role", "Owner", "LogicalDeviceId", "PhysicalBinding", "StartPositionSteps", "Limits",
                "Metric", "VerifiedUtc", "ValidUntilUtc", "Confidence"
            ]);
            RequireProperties(GetProperty(focus, "PhysicalBinding"), label + ".PhysicalBinding", [
                "Mechanism", "ConnectionEndpoint", "HardwareInstanceId", "TopologyPath"
            ]);
            RequireProperties(GetProperty(focus, "Limits"), label + ".Limits", [
                "MinimumPositionSteps", "MaximumPositionSteps", "MaximumSingleMoveSteps", "MaximumCumulativeMoveSteps",
                "ApproachDirection", "BacklashCompensationSteps"
            ]);
            RequireProperties(GetProperty(focus, "Metric"), label + ".Metric", [
                "Kind", "SourceCameraStableDeviceId", "Value", "Unit", "EvidenceSha256"
            ]);
            index++;
        }
    }

    private static void RequireCommissioningDefinitionJsonShape(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        RequireProperties(root, "Commissioning measurement definition", [
            "SchemaVersion", "PresetId", "CreatedUtc", "ValidUntilUtc", "Provenance",
            "Phd2CalibrationTimestampUtc", "TelescopeDeviceId", "G3CameraStableId", "G3Binning",
            "G3ExposureMilliseconds", "G3GainPercent", "G3ExpectedWcsFlipped", "G3SaturationAdu", "Slit",
            "MountTransform", "Motion", "Environment", "FineMotionAuthority", "Phd2SlitPlacement",
            "GhostAssistanceMode", "GhostAssistance", "SlitWheelIdentity"
        ]);
        RequireIntegerProperty(root, "FineMotionAuthority", "Commissioning measurement definition");
        RequireIntegerProperty(root, "GhostAssistanceMode", "Commissioning measurement definition");
        var phd2Placement = GetProperty(root, "Phd2SlitPlacement");
        RequireProperties(phd2Placement, "Commissioning Phd2SlitPlacement", [
            "InstallationEpochId", "LockedTopologyFingerprintSha256", "CoordinateDomain",
            "SensorWidthPixels", "SensorHeightPixels", "RoiX", "RoiY", "RoiWidth", "RoiHeight",
            "SensorRotationDegrees", "RotationAuthority", "PierSide", "GuideMode",
            "ExpectedGuidingExposureMilliseconds", "MaximumStagePixels", "MaximumCumulativePixels",
            "MaximumAttempts", "MaximumElapsedSeconds", "MaximumStageSeconds", "MaximumMeasurementAgeSeconds",
            "MaximumSafetySnapshotAgeSeconds", "LockPreconditionTolerancePixels", "LockVerificationTolerancePixels",
            "TargetOnSlitTolerancePixels", "MaximumAcquisitionResidualPixels", "MinimumOffSlitGuideDistancePixels",
            "MinimumOffSlitGuideTargetSeparationPixels", "MaximumGuideLockResidualPixels",
            "MaximumDegradedDirectTargetGuideLockResidualPixels", "MaximumDirectTargetCentroidSeparationPixels",
            "MinimumFluxMetric", "MaximumFluxMetric", "MinimumAltitudeDegrees",
            "MinimumAxisRatePixelsPerSecond", "MaximumAxisRatePixelsPerSecond", "RaBidirectionalRateRatio",
            "DecBidirectionalRateRatio", "CalibrationProcessEvidenceComplete", "CalibrationTopologyEvidenceComplete",
            "CalibrationPierSideEvidenceComplete", "FreshLoopFrameTimeoutSeconds", "FreshGuidingFrameTimeoutSeconds",
            "TargetSearchRadiusPixels", "GuideSearchRadiusPixels", "MinimumTargetSignalToNoise",
            "MinimumGuideSignalToNoise", "MinimumTargetUniquenessRatio", "SlitMaximumPerpendicularSearchPixels",
            "SlitMaximumAngleSearchDegrees", "SlitMinimumContrastSigma", "MaximumResidualGrowthPixels",
            "CalibrationQualityPolicy", "CalibrationQualityPolicySha256", "OffSlitGuidingExposureMilliseconds",
            "DirectTargetGuidingExposureMilliseconds"
        ]);
        RequireIntegerProperty(phd2Placement, "CoordinateDomain", "Commissioning Phd2SlitPlacement");
        RequireIntegerProperty(phd2Placement, "RotationAuthority", "Commissioning Phd2SlitPlacement");
        RequireIntegerProperty(phd2Placement, "GuideMode", "Commissioning Phd2SlitPlacement");
        RequireProperties(GetProperty(phd2Placement, "CalibrationQualityPolicy"), "Commissioning PHD2 calibration-quality policy", [
            "PolicyId", "ExcellentMaximumAge", "QualifiedMaximumAge", "DegradedMaximumAge",
            "ExcellentMaximumOrthogonalityErrorDegrees", "QualifiedMaximumOrthogonalityErrorDegrees",
            "DegradedMaximumOrthogonalityErrorDegrees", "ExcellentMaximumBidirectionalRateRatio",
            "QualifiedMaximumBidirectionalRateRatio", "DegradedMaximumBidirectionalRateRatio",
            "ExcellentMaximumCrossAxisRateRatio", "QualifiedMaximumCrossAxisRateRatio",
            "DegradedMaximumCrossAxisRateRatio", "ExcellentMaximumDroppedFrameFraction",
            "QualifiedMaximumDroppedFrameFraction", "DegradedMaximumDroppedFrameFraction",
            "MaximumSettleEvidenceAge", "MaximumResidualEvidenceAge", "QualifiedMaximumLockShiftScale",
            "DegradedMaximumLockShiftScale", "QualifiedResidualToleranceScale",
            "DegradedResidualToleranceScale", "RequiredFreshResidualsPerLockShiftStage"
        ]);
        var ghost = GetProperty(root, "GhostAssistance");
        if (ghost.ValueKind != JsonValueKind.Null)
            RequireGhostAssistanceJsonShape(ghost);
        RequireProperties(GetProperty(root, "Slit"), "Commissioning Slit", [
            "CalibrationId", "MeasuredUtc", "EvidencePath", "EvidenceSha256", "AcquisitionX", "AcquisitionY",
            "AngleDegrees", "LengthPixels", "WidthPixels", "UncertaintyPixels"
        ]);
        var slitIdentity = GetProperty(root, "SlitWheelIdentity");
        RequireProperties(slitIdentity, "Commissioning SlitWheelIdentity", [
            "CalibrationId", "InstallationEpochId", "ImageWidthPixels", "ImageHeightPixels",
            "MaximumNormalizedResidual", "MinimumRunnerUpSeparationSigma", "Fingerprints",
            "MeasurementModelId", "ShortExposureMilliseconds", "LongExposureMilliseconds",
            "EdgePsfAlphaPixels", "EdgePsfBeta"
        ]);
        var slitFingerprints = GetProperty(slitIdentity, "Fingerprints");
        if (slitFingerprints.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Commissioning SlitWheelIdentity.Fingerprints must be an array.");
        var slitFingerprintIndex = 0;
        foreach (var fingerprint in slitFingerprints.EnumerateArray())
        {
            RequireProperties(fingerprint, $"Commissioning SlitWheelIdentity.Fingerprints[{slitFingerprintIndex}]", [
                "WheelPosition", "SlitLabel", "NominalWidthMicrometers", "MeasuredWidthPixels",
                "WidthUncertaintyPixels", "MeasuredUtc", "EvidencePath", "EvidenceSha256", "Resolution",
                "ReflectiveEdgeToApertureCenterPixels", "SecondaryEdgeAmplitudeRatio",
                "ShortExposureEvidencePath", "ShortExposureEvidenceSha256",
                "LongExposureEvidencePath", "LongExposureEvidenceSha256"
            ]);
            slitFingerprintIndex++;
        }
        var mount = GetProperty(root, "MountTransform");
        RequireProperties(mount, "Commissioning MountTransform", [
            "CalibrationId", "MeasuredUtc", "PierSide", "MaximumResidualArcseconds", "MaximumConditionEstimate",
            "MaximumSampleMotionArcseconds", "Samples"
        ]);
        var samples = GetProperty(mount, "Samples");
        if (samples.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Commissioning MountTransform.Samples must be an array.");
        var index = 0;
        foreach (var sample in samples.EnumerateArray())
        {
            RequireProperties(sample, $"Commissioning MountTransform.Samples[{index}]", [
                "CommandedRaArcseconds", "CommandedDecArcseconds", "MeasuredPixelShiftX", "MeasuredPixelShiftY"
            ]);
            index++;
        }
        RequireProperties(GetProperty(root, "Motion"), "Commissioning Motion", [
            "MaximumSingleCorrectionArcseconds", "MaximumCumulativeCorrectionArcseconds", "MaximumCorrectionAttempts", "MaximumAcquisitionMinutes"
        ]);
        RequireProperties(GetProperty(root, "Environment"), "Commissioning Environment", [
            "RequireSafetyMonitor", "RequireOpenDomeOrRoof", "RequireWeatherData", "MaximumCloudCoverPercent",
            "MaximumHumidityPercent", "MaximumWindSpeedMetersPerSecond"
        ]);
    }

    private static void RequireProperties(JsonElement element, string label, IReadOnlyList<string> names)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"{label} must be a JSON object.");
        foreach (var name in names)
        {
            if (!TryGetProperty(element, name, out _)) throw new InvalidDataException($"{label} is missing explicit property '{name}'.");
        }
    }

    private static void RequireGhostAssistanceJsonShape(JsonElement ghost)
    {
        RequireProperties(ghost, "Commissioning GhostAssistance", [
            "SchemaVersion", "BindingId", "Calibration", "MatchPolicy", "MatchPolicySha256",
            "ExtractionPolicy", "ExtractionPolicySha256", "RuntimeFingerprint", "MaximumExternalIdentityAge",
            "MaximumCatalogCoordinateMismatchArcseconds", "MaximumQhyTargetResidualArcseconds",
            "MinimumC11FocusConfidence"
        ]);
        var calibration = GetProperty(ghost, "Calibration");
        RequireProperties(calibration, "Commissioning GhostAssistance.Calibration", [
            "SchemaVersion", "CalibrationId", "InstallationEpochId", "CameraStableId", "Phd2ProfileId",
            "ExtractorKind", "ExtractorVersion", "ExtractionPolicyId", "ExtractionPolicySha256",
            "OpticalTopologySha256", "Detector", "OrientationFingerprintSha256", "OrientationDegrees",
            "PierSide", "Gain", "MinimumExposureMilliseconds", "MaximumExposureMilliseconds", "CreatedUtc",
            "ValidUntilUtc", "CalibrationRmsResidualPixels", "CalibrationMaximumResidualPixels",
            "TargetSystematicCovariancePixelsSquared", "Features", "CalibrationEvidenceSha256", "CalibrationSha256"
        ]);
        RequireProperties(GetProperty(calibration, "Detector"), "Commissioning GhostAssistance.Calibration.Detector", [
            "RoiX", "RoiY", "RoiWidth", "RoiHeight", "BinningX", "BinningY"
        ]);
        RequireCovarianceShape(GetProperty(calibration, "TargetSystematicCovariancePixelsSquared"),
            "Commissioning GhostAssistance.Calibration.TargetSystematicCovariancePixelsSquared");
        var features = GetProperty(calibration, "Features");
        if (features.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Commissioning GhostAssistance.Calibration.Features must be an array.");
        var featureIndex = 0;
        foreach (var feature in features.EnumerateArray())
        {
            var label = $"Commissioning GhostAssistance.Calibration.Features[{featureIndex}]";
            RequireProperties(feature, label, ["FeatureId", "OffsetFromTarget", "RelativeFlux", "OffsetCovariancePixelsSquared"]);
            RequireProperties(GetProperty(feature, "OffsetFromTarget"), label + ".OffsetFromTarget", ["X", "Y"]);
            RequireCovarianceShape(GetProperty(feature, "OffsetCovariancePixelsSquared"), label + ".OffsetCovariancePixelsSquared");
            featureIndex++;
        }

        RequireProperties(GetProperty(ghost, "MatchPolicy"), "Commissioning GhostAssistance.MatchPolicy", [
            "SchemaVersion", "PolicyId", "MaximumCalibrationAge", "MaximumFrameAge", "MaximumFrameSpan",
            "MaximumOrientationDifferenceDegrees", "MinimumFrameCount", "MinimumMatchedFeatures",
            "MaximumFeatureResidualPixels", "MaximumRelativeFluxLogResidual",
            "MaximumExposureNormalizedFluxLogScatter", "MaximumCommonMotionResidualPixels",
            "MaximumRegisteredTargetScatterPixels", "MinimumUniquenessLikelihoodRatio",
            "CandidateMergeRadiusPixels", "EdgeMarginPixels", "MaximumTargetUncertaintyPixels"
        ]);
        var extraction = GetProperty(ghost, "ExtractionPolicy");
        RequireProperties(extraction, "Commissioning GhostAssistance.ExtractionPolicy", [
            "SchemaVersion", "PolicyId", "ExtractorKind", "ExtractorVersion", "StarDetection",
            "MinimumSignalToNoise", "MaximumGhostEllipticity", "MinimumCentroidSigmaPixels",
            "MaximumCentroidSigmaPixels"
        ]);
        RequireProperties(GetProperty(extraction, "StarDetection"), "Commissioning GhostAssistance.ExtractionPolicy.StarDetection", [
            "DetectionSigma", "CentroidRadiusPixels", "EdgeMarginPixels", "MaximumCandidates",
            "MaximumEllipticity", "MaximumSaturatedFraction"
        ]);
        RequireProperties(GetProperty(ghost, "RuntimeFingerprint"), "Commissioning GhostAssistance.RuntimeFingerprint", [
            "InstallationEpochId", "OpticalTopologySha256", "OrientationFingerprintSha256", "OrientationDegrees"
        ]);
    }

    private static void RequireCovarianceShape(JsonElement covariance, string label) =>
        RequireProperties(covariance, label, ["XX", "XY", "YY"]);

    private static void RequireIntegerProperty(JsonElement element, string name, string label)
    {
        var value = GetProperty(element, name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out _))
            throw new InvalidDataException($"{label}.{name} must be an integer JSON number, not an enum name.");
    }

    private static JsonElement GetProperty(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var value)) throw new InvalidDataException($"JSON property '{name}' is missing.");
        return value;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    private static string ResolveEvidencePath(string path, string definitionDirectory) =>
        Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(definitionDirectory, path));

    private static bool FinitePercent(double value) => double.IsFinite(value) && value is >= 0 and <= 100;
    private static bool SameHash(string left, string right) => string.Equals(ArtifactIO.NormalizeHash(left), ArtifactIO.NormalizeHash(right), StringComparison.Ordinal);

    private static void RequireValid(Phd2ProfileBindingValidation validation, string label)
    {
        var issues = new List<string>();
        AddValidationIssues(validation, label, issues);
        RequireNoIssues(issues, label);
    }

    private static void AddValidationIssues(Phd2ProfileBindingValidation validation, string label, List<string> issues)
    {
        issues.AddRange(validation.Failures.Select(item => $"{label}: {item}"));
        issues.AddRange(validation.IndeterminateReasons.Select(item => $"{label}: {item}"));
        if (validation.Evidence is null) issues.Add($"{label}: no evidence snapshot was produced.");
    }

    private static void RequireNoIssues(IReadOnlyList<string> issues, string label)
    {
        if (issues.Count > 0) throw new InvalidDataException($"{label} is not valid:{Environment.NewLine}- {string.Join(Environment.NewLine + "- ", issues)}");
    }

    private static ValidationSummary Summary(string kind, string path, string fileSha, IReadOnlyList<string> issues, IReadOnlyDictionary<string, string> bindings) =>
        new(kind, Path.GetFullPath(path), fileSha, issues.Count == 0, issues, bindings);

    private static ValidationSummary Invalid(string kind, string path, string fileSha, string issue) =>
        Summary(kind, path, fileSha, [issue], new Dictionary<string, string>());

    private sealed record LoadedCommissioningInputs(
        CommissioningMeasurementDefinition Definition,
        NightSetupRecord NightSetup,
        Phd2ProfileBindingSnapshot Phd2Evidence,
        string NightSetupSha256,
        string Phd2ProfileEvidenceSha256);

    private enum CommissioningFineMotionAuthority
    {
        IndependentMountTransform = 0,
        Phd2CalibrationLockShift = 1,
        AutoPreferPhd2ThenIndependent = 2,
    }

    // Keep declaration order and property casing identical to the anonymous
    // object serialized by RealCommissioningPresetLoader.ValidateHardwareFingerprint.
    private sealed record HardwareFingerprintCanonical(
        string AtrCameraStableId,
        string G3CameraStableId,
        string QhyCameraStableId,
        string TelescopeDeviceId,
        string NightSetupId,
        string NightSetupSha256,
        string Phd2ProfileEvidenceSha256);

    // Keep declaration order and property casing identical to
    // WindowsPhd2ProfileEvidence.ProfileEvidencePayload.
    private sealed record Phd2ProfileEvidenceCanonical(
        int ProfileId,
        string ProfileName,
        string CameraName,
        IReadOnlyList<string> CameraStableIds,
        string MountName,
        int? Binning,
        int? GainPercent,
        int? FocalLengthMillimeters,
        int? CameraBitsPerPixel);
}
