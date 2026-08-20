using System.Text.Json;
using UvexAdv.Commissioning.Tool;
using UvexAdv.Observatory;
using UvexAdv.Phd2;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class CommissioningToolSchema4CompatibilityTests
{
    [Fact]
    public void ToolJsonBytesDeserializeWithProductionPresetContractAndValidator()
    {
        var profile = CreateProfileEvidence();
        var policy = Phd2CalibrationQualityPolicy.Default;
        var placement = CreatePlacement(profile, policy);
        var toolPreset = CreateToolPreset(profile, placement, ghost: null);

        var bytes = JsonSerializer.SerializeToUtf8Bytes(toolPreset, ArtifactIO.CommissioningPresetJsonOptions);
        using (var document = JsonDocument.Parse(bytes))
        {
            Assert.Equal(JsonValueKind.Number, document.RootElement.GetProperty("FineMotionAuthority").ValueKind);
            var placementJson = document.RootElement.GetProperty("Phd2SlitPlacement");
            Assert.Equal(JsonValueKind.Number, placementJson.GetProperty("CoordinateDomain").ValueKind);
            Assert.Equal(JsonValueKind.Number, placementJson.GetProperty("RotationAuthority").ValueKind);
            Assert.Equal(JsonValueKind.Number, placementJson.GetProperty("GuideMode").ValueKind);
        }

        var loaded = JsonSerializer.Deserialize<RealCommissioningPreset>(
            bytes,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(loaded);
        Assert.Equal(CommissioningPresetContract.CurrentSchemaVersion, loaded!.SchemaVersion);
        Assert.Equal(RealSlitPlacementAuthority.AutoPreferPhd2ThenIndependent, loaded.FineMotionAuthority);
        Assert.NotNull(loaded.Phd2SlitPlacement);
        Assert.Equal(Phd2ImageCoordinateDomain.FullSensorCoordinates, loaded.Phd2SlitPlacement!.CoordinateDomain);
        Assert.Equal(Phd2SensorRotationAuthority.QualifiedPhd2Calibration, loaded.Phd2SlitPlacement.RotationAuthority);
        Assert.Equal(Phd2SlitGuideMode.AutoPreferOffSlitThenDirectTarget, loaded.Phd2SlitPlacement.GuideMode);
        Assert.Equal(2_000, loaded.Phd2SlitPlacement.OffSlitGuidingExposureMilliseconds);
        Assert.Equal(250, loaded.Phd2SlitPlacement.DirectTargetGuidingExposureMilliseconds);
        Assert.Empty(loaded.Phd2SlitPlacement.Validate());
        Assert.NotNull(loaded.SlitWheelIdentity);
        Assert.Empty(loaded.SlitWheelIdentity!.Validate());
        Assert.Equal(4, loaded.SlitWheelIdentity.Fingerprints.Count);
        Assert.Null(loaded.GhostAssistance);
    }

    [Fact]
    public void ToolGhostJsonBytesDeserializeWithProductionPresetContractAndValidator()
    {
        var profile = CreateProfileEvidence();
        var placement = CreatePlacement(profile, Phd2CalibrationQualityPolicy.Default);
        var toolGhost = CreateGhostAssistance();
        var toolPreset = CreateToolPreset(profile, placement, toolGhost);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(toolPreset, ArtifactIO.CommissioningPresetJsonOptions);

        using (var document = JsonDocument.Parse(bytes))
        {
            var ghost = document.RootElement.GetProperty("GhostAssistance");
            Assert.Equal(JsonValueKind.Number, ghost.GetProperty("Calibration").GetProperty("ExtractorKind").ValueKind);
            Assert.Equal(JsonValueKind.Number, ghost.GetProperty("ExtractionPolicy").GetProperty("ExtractorKind").ValueKind);
        }

        var loaded = JsonSerializer.Deserialize<RealCommissioningPreset>(
            bytes,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(loaded?.GhostAssistance);
        Assert.Empty(loaded!.GhostAssistance!.Validate());
        Assert.Equal(toolGhost.BindingId, loaded.GhostAssistance.BindingId);
        Assert.Equal(toolGhost.Calibration!.CalibrationSha256, loaded.GhostAssistance.Calibration.CalibrationSha256);
        Assert.Equal(toolGhost.ExtractionPolicySha256, loaded.GhostAssistance.ExtractionPolicySha256);
    }

    [Fact]
    public void ToolAndProductionValidatorsRejectTheSamePolicyHashMismatch()
    {
        var profile = CreateProfileEvidence();
        var policy = Phd2CalibrationQualityPolicy.Default;
        var toolPlacement = CreatePlacement(profile, policy) with
        {
            CalibrationQualityPolicySha256 = new string('0', 64),
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(toolPlacement, ArtifactIO.JsonOptions);
        var productionPlacement = JsonSerializer.Deserialize<Phd2SlitPlacementCommissioningPreset>(
            bytes,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        var toolIssues = toolPlacement.Validate();
        var productionIssues = Assert.IsType<Phd2SlitPlacementCommissioningPreset>(productionPlacement).Validate();

        Assert.Contains(toolIssues, issue => issue.Contains("policy SHA-256", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(toolIssues, productionIssues);
    }

    [Fact]
    public void BindingsImportAcceptsDefinedNumericOrNamedGhostModeOnly()
    {
        using var autoJson = JsonDocument.Parse("1");
        using var invalidJson = JsonDocument.Parse("99");
        using var stringJson = JsonDocument.Parse("\"AutoIfValidElseSkip\"");
        using var invalidStringJson = JsonDocument.Parse("\"NotAGhostMode\"");

        var converted = CommissioningBindingValueConverter.Convert(
            autoJson.RootElement,
            typeof(GhostAssistanceMode),
            nameof(UvexPluginSettings.GhostAssistanceMode));
        var convertedName = CommissioningBindingValueConverter.Convert(
            stringJson.RootElement,
            typeof(GhostAssistanceMode),
            nameof(UvexPluginSettings.GhostAssistanceMode));

        Assert.Equal(GhostAssistanceMode.AutoIfValidElseSkip, converted);
        Assert.Equal(GhostAssistanceMode.AutoIfValidElseSkip, convertedName);
        Assert.Throws<InvalidDataException>(() => CommissioningBindingValueConverter.Convert(
            invalidJson.RootElement,
            typeof(GhostAssistanceMode),
            nameof(UvexPluginSettings.GhostAssistanceMode)));
        Assert.Throws<InvalidDataException>(() => CommissioningBindingValueConverter.Convert(
            invalidStringJson.RootElement,
            typeof(GhostAssistanceMode),
            nameof(UvexPluginSettings.GhostAssistanceMode)));
    }

    [Fact]
    public void ToolAndProductionGhostValidatorsRejectTheSamePolicyHashMismatch()
    {
        var toolGhost = CreateGhostAssistance() with { MatchPolicySha256 = string.Empty };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(toolGhost, ArtifactIO.CommissioningPresetJsonOptions);
        var productionGhost = JsonSerializer.Deserialize<GhostAssistanceCommissioningPreset>(
            bytes,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        var toolIssues = toolGhost.Validate();
        var productionIssues = Assert.IsType<GhostAssistanceCommissioningPreset>(productionGhost).Validate();

        Assert.Contains(toolIssues, issue => issue.Contains("match-policy SHA-256", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(toolIssues, productionIssues);
    }

    private static Phd2ProfileBindingSnapshot CreateProfileEvidence()
    {
        var evidence = new Phd2ProfileBindingSnapshot(
            2,
            "profile-name",
            "ToupTek Camera",
            ["g3-stable-id"],
            "OnStep Telescope (ASCOM)",
            1,
            100,
            2_150,
            16,
            @"HKCU\Software\StarkLabs\PHDGuidingV2\profile\2",
            string.Empty,
            DateTimeOffset.UtcNow.AddMinutes(-2));
        return evidence with { Sha256 = EvidenceBuilders.ComputePhd2ProfileEvidenceSha256(evidence) };
    }

    private static CommissioningPresetContract CreateToolPreset(
        Phd2ProfileBindingSnapshot profile,
        Phd2SlitPlacementContract placement,
        GhostAssistanceContract? ghost) => new(
        CommissioningPresetContract.CurrentSchemaVersion,
        "preset-compatibility-test",
        DateTimeOffset.UtcNow.AddMinutes(-1),
        "synthetic offline JSON compatibility test",
        "night-1",
        new string('A', 64),
        profile.Sha256,
        DateTimeOffset.UtcNow.AddMinutes(-5).ToString("O"),
        "ASCOM.OnStep.Telescope",
        "g3-stable-id",
        1,
        10_000,
        100,
        false,
        new SlitGeometryContract("slit-1", 900, 500, 3, 1_000, 3, 0.5),
        new MountTransformContract("mount-1", "East", 0, 2, -2, 0, 0.5),
        new MotionLimitContract(30, 120, 8, 10),
        new EnvironmentContract(true, true, true, 50, 90, 12),
        DateTimeOffset.UtcNow.AddDays(1),
        new HardwareFingerprintContract(
            "atr-stable-id", "g3-stable-id", "qhy-stable-id", "ASCOM.OnStep.Telescope",
            "night-1", new string('A', 64), profile.Sha256, new string('B', 64)),
        4095,
        (int)RealSlitPlacementAuthority.AutoPreferPhd2ThenIndependent,
        placement,
        ghost,
        CreateSlitWheelIdentity(placement.InstallationEpochId));

    private static SlitWheelIdentityCalibration CreateSlitWheelIdentity(string installationEpochId)
    {
        static SlitWidthFingerprint Fingerprint(int position, string label, double nominal, double pixels, double uncertainty) =>
            new(
                position,
                label,
                nominal,
                pixels,
                uncertainty,
                DateTimeOffset.UtcNow.AddHours(-1),
                new string((char)('0' + position), 64),
                SlitDarkApertureResolution.DirectTwoEdge,
                pixels / 2,
                0.1,
                new string((char)('4' + position), 64),
                new string((char)('A' + position - 1), 64));
        return new SlitWheelIdentityCalibration(
            SlitWheelIdentityCalibration.CurrentSchemaVersion,
            "slit-wheel-identity-1",
            installationEpochId,
            "g3-stable-id",
            1,
            1,
            1920,
            1080,
            3,
            2,
            [
                Fingerprint(1, "300um", 300, 70, 1.0),
                Fingerprint(2, "15um", 15, 9, 0.6),
                Fingerprint(3, "25um", 25, 15, 0.7),
                Fingerprint(4, "35um", 35, 22, 0.8),
            ],
            string.Empty).WithComputedSha256();
    }

    private static Phd2SlitPlacementContract CreatePlacement(
        Phd2ProfileBindingSnapshot profile,
        Phd2CalibrationQualityPolicy policy)
    {
        var placement = new Phd2SlitPlacementContract(
            "installation-epoch-1",
            new string('0', 64),
            (int)Phd2ImageCoordinateDomain.FullSensorCoordinates,
            1920,
            1080,
            0,
            0,
            1920,
            1080,
            12.5,
            (int)Phd2SensorRotationAuthority.QualifiedPhd2Calibration,
            "East",
            (int)Phd2SlitGuideMode.AutoPreferOffSlitThenDirectTarget,
            2_000,
            25,
            100,
            8,
            300,
            30,
            5,
            2,
            1,
            1,
            2,
            20,
            5,
            5,
            2,
            4,
            2,
            10,
            100_000,
            30,
            0.1,
            50,
            1.2,
            1.2,
            true,
            true,
            true,
            10,
            10,
            100,
            200,
            5,
            5,
            1.5,
            20,
            15,
            3,
            2,
            policy,
            Phd2SlitPlacementContract.ComputePolicySha256(policy),
            2_000,
            250);
        return placement with
        {
            LockedTopologyFingerprintSha256 = placement.ComputeTopologyFingerprintSha256(
                profile,
                "g3-stable-id",
                1),
        };
    }

    private static GhostAssistanceContract CreateGhostAssistance()
    {
        var extraction = new GhostSourceExtractionPolicy(
            GhostSourceExtractionPolicy.CurrentSchemaVersion,
            "ghost-extraction-v1",
            GhostFeatureExtractorKind.PointSourceStarFieldV1,
            GhostSourceExtractionPolicy.CurrentBackendVersion,
            new StarDetectionOptions(5, 4, 12, 100, 0.65, 0.02),
            5,
            0.65,
            0.1,
            3);
        var extractionSha = extraction.ComputeContentSha256();
        var calibration = new GhostTemplateCalibration(
            GhostTemplateCalibration.CurrentSchemaVersion,
            "ghost-calibration-1",
            "installation-epoch-1",
            "g3-stable-id",
            "2",
            extraction.ExtractorKind,
            extraction.ExtractorVersion,
            extraction.PolicyId,
            extractionSha,
            new string('4', 64),
            new GhostDetectorGeometry(0, 0, 1920, 1080, 1, 1),
            new string('5', 64),
            12.5,
            "East",
            100,
            500,
            20_000,
            DateTimeOffset.UtcNow.AddHours(-1),
            DateTimeOffset.UtcNow.AddDays(2),
            1,
            2,
            new GhostCovariance2D(1, 0, 1),
            [new GhostTemplateFeature("ghost-a", new PixelPoint(20, 10), 0.2, new GhostCovariance2D(1, 0, 1))],
            new string('6', 64),
            string.Empty).WithComputedSha256();
        var match = new GhostTemplatePolicy(
            GhostTemplatePolicy.CurrentSchemaVersion,
            "ghost-match-v1",
            TimeSpan.FromDays(7),
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(1),
            2,
            2,
            1,
            5,
            2,
            2,
            2,
            2,
            1.5,
            2,
            10,
            5);
        return new GhostAssistanceContract(
            GhostAssistanceContract.CurrentSchemaVersion,
            "ghost-binding-1",
            calibration,
            match,
            GhostAssistanceContract.ComputeContentSha256(match),
            extraction,
            extractionSha,
            new GhostRuntimeFingerprintContract(
                calibration.InstallationEpochId,
                calibration.OpticalTopologySha256,
                calibration.OrientationFingerprintSha256,
                calibration.OrientationDegrees),
            TimeSpan.FromMinutes(5),
            30,
            60,
            0.9);
    }
}
