using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UvexAdv.Observatory;
using UvexAdv.Phd2;
using Xunit;

namespace UvexAdv.Commissioning.Tool.Tests;

public sealed class EvidenceBuildersTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "uvex-commissioning-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void HardwareFingerprintMatchesNinaLoaderCanonicalSerialization()
    {
        var fingerprint = new HardwareFingerprintContract(
            "atr\\instance", "g3\\instance", "qhy-id", "ASCOM.OnStep.Telescope", "night-1",
            new string('a', 64), new string('b', 64), string.Empty);

        var actual = EvidenceBuilders.ComputeHardwareFingerprintSha256(fingerprint);
        var canonical = JsonSerializer.Serialize(new
        {
            fingerprint.AtrCameraStableId,
            fingerprint.G3CameraStableId,
            fingerprint.QhyCameraStableId,
            fingerprint.TelescopeDeviceId,
            fingerprint.NightSetupId,
            NightSetupSha256 = new string('A', 64),
            Phd2ProfileEvidenceSha256 = new string('B', 64),
        });
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void PhdEvidenceHashMatchesRegistryPayloadCanonicalSerialization()
    {
        var evidence = CreatePhdEvidence(hash: string.Empty);

        var actual = EvidenceBuilders.ComputePhd2ProfileEvidenceSha256(evidence);
        var canonical = JsonSerializer.Serialize(new
        {
            evidence.ProfileId,
            evidence.ProfileName,
            evidence.CameraName,
            evidence.CameraStableIds,
            evidence.MountName,
            evidence.Binning,
            evidence.GainPercent,
            evidence.FocalLengthMillimeters,
            evidence.CameraBitsPerPixel,
        });
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task FullOfflineEvidenceChainCreatesAndRevalidatesSchema4Preset()
    {
        Directory.CreateDirectory(root);
        var setup = CreateNightSetup();
        var setupDefinition = await ArtifactIO.WriteJsonAtomicallyAsync(Path.Combine(root, "night-definition.json"), setup, false);
        var locked = await EvidenceBuilders.CreateNightSetupAsync(
            setupDefinition.AbsolutePath, setupDefinition.Sha256, Path.Combine(root, "night-setup.json"), false);

        var phd = CreatePhdEvidence(hash: string.Empty);
        phd = phd with { Sha256 = EvidenceBuilders.ComputePhd2ProfileEvidenceSha256(phd) };
        var phdArtifact = await ArtifactIO.WriteJsonAtomicallyAsync(Path.Combine(root, "phd2.json"), phd, false);

        var slitEvidence = await ArtifactIO.WriteTextAtomicallyAsync(Path.Combine(root, "slit-measurement.txt"), "measured slit geometry", false);
        var definition = CreateDefinition(slitEvidence, phd);
        var definitionArtifact = await ArtifactIO.WriteJsonAtomicallyAsync(Path.Combine(root, "commissioning-definition.json"), definition, false);
        var inputs = new CommissioningInputFiles(
            definitionArtifact.AbsolutePath, definitionArtifact.Sha256,
            locked.Artifact.AbsolutePath, locked.Artifact.Sha256,
            phdArtifact.AbsolutePath, phdArtifact.Sha256, phd.Sha256);

        var created = await EvidenceBuilders.CreateCommissioningPresetAsync(inputs, Path.Combine(root, "preset.json"), false, verifyCurrentPhd2Registry: false);
        var validation = await EvidenceBuilders.ValidateCommissioningPresetAsync(inputs, created.Artifact.AbsolutePath, created.Artifact.Sha256, verifyCurrentPhd2Registry: false);

        Assert.True(validation.Valid, string.Join(Environment.NewLine, validation.Issues));
        Assert.Equal(CommissioningPresetContract.CurrentSchemaVersion, created.Preset.SchemaVersion);
        Assert.Equal(2, created.Preset.FineMotionAuthority);
        Assert.NotNull(created.Preset.Phd2SlitPlacement);
        Assert.Empty(created.Preset.Phd2SlitPlacement!.Validate());
        Assert.Equal(2_000, created.Preset.Phd2SlitPlacement.OffSlitGuidingExposureMilliseconds);
        Assert.Equal(250, created.Preset.Phd2SlitPlacement.DirectTargetGuidingExposureMilliseconds);
        Assert.Null(created.Preset.GhostAssistance);
        Assert.Equal(-2, created.Preset.MountTransform!.DecArcsecondsPerPixelX, 8);
        Assert.Equal(2, created.Preset.MountTransform.RaArcsecondsPerPixelY, 8);
        Assert.Equal(created.Preset.HardwareFingerprint!.Sha256, created.Bindings.HardwareFingerprintSha256);
        Assert.Equal(created.Artifact.Sha256, created.Bindings.PresetSha256);
        Assert.Equal(3, locked.Setup.FocusDomains!.Count);
        Assert.Equal(4095, locked.Setup.G3SaturationAdu);
        Assert.Equal(4095, created.Preset.G3SaturationAdu);
        Assert.Equal(4095, created.Bindings.NinaProfileValues["G3SaturationAdu"]);
        Assert.Equal(false, created.Bindings.NinaProfileValues["ObservationUseRealMode"]);
        Assert.Equal(false, created.Bindings.NinaProfileValues["RealModeCommissioned"]);
        Assert.Equal(false, created.Bindings.NinaProfileValues["AllowDegradedSupervisedScience"]);
        Assert.Equal(false, created.Bindings.NinaProfileValues["WeakSupervisionEnabled"]);
        Assert.Equal(true, created.Bindings.NinaProfileValues["RequireOpenOpticalCover"]);
        Assert.Equal("NinaSafetyStack", created.Bindings.NinaProfileValues["PreparationSafetyCapabilityPreset"]);
        Assert.Equal(0, created.Bindings.NinaProfileValues["GhostAssistanceMode"]);
        Assert.Equal(false, created.Bindings.NinaProfileValues["QhyG3FastPairEnabled"]);
        Assert.Equal(QhyG3FastPairPolicy.CurrentSchemaVersion, created.Bindings.NinaProfileValues["QhyG3FastPairSchemaVersion"]);
        Assert.Equal("qhy-g3-fast-pair-v1", created.Bindings.NinaProfileValues["QhyG3FastPairPolicyId"]);
        Assert.Equal(2d, created.Bindings.NinaProfileValues["QhyG3FastPairExposureSeconds"]);
        Assert.Equal(15d, created.Bindings.NinaProfileValues["QhyG3FastPairMaximumCachedAgeSeconds"]);
        Assert.Equal(20d, created.Bindings.NinaProfileValues["QhyG3FastPairMaximumMidpointSeparationSeconds"]);
        Assert.Equal(30d, created.Bindings.NinaProfileValues["QhyG3FastPairMaximumWallClockSeconds"]);
        Assert.Equal(2d, created.Bindings.NinaProfileValues["QhyG3FastPairMaximumMountSpanArcseconds"]);
        Assert.Equal(24d, created.Bindings.NinaProfileValues["QhyG3FastPairCandidateValidityHours"]);
        Assert.Equal(20d, created.Bindings.NinaProfileValues["QhyG3FastPairMaximumCandidateUncertaintyArcseconds"]);

        var weakDefinition = definition with
        {
            PresetId = "preset-weak-supervision",
            Environment = new EnvironmentContract(false, false, false, 50, 90, 12),
        };
        var weakDefinitionArtifact = await ArtifactIO.WriteJsonAtomicallyAsync(
            Path.Combine(root, "commissioning-definition-weak.json"), weakDefinition, false);
        var weakInputs = inputs with
        {
            DefinitionPath = weakDefinitionArtifact.AbsolutePath,
            DefinitionSha256 = weakDefinitionArtifact.Sha256,
        };
        var weakCreated = await EvidenceBuilders.CreateCommissioningPresetAsync(
            weakInputs, Path.Combine(root, "preset-weak.json"), false, verifyCurrentPhd2Registry: false);
        Assert.Equal(true, weakCreated.Bindings.NinaProfileValues["WeakSupervisionEnabled"]);
        Assert.Equal(false, weakCreated.Bindings.NinaProfileValues["RequireOpenOpticalCover"]);
        Assert.Equal("OperatorWeakSupervision", weakCreated.Bindings.NinaProfileValues["PreparationSafetyCapabilityPreset"]);
        using (var presetJson = JsonDocument.Parse(await File.ReadAllBytesAsync(created.Artifact.AbsolutePath)))
        {
            Assert.Equal(JsonValueKind.Number, presetJson.RootElement.GetProperty("FineMotionAuthority").ValueKind);
            Assert.Equal(JsonValueKind.Null, presetJson.RootElement.GetProperty("GhostAssistance").ValueKind);
            var placement = presetJson.RootElement.GetProperty("Phd2SlitPlacement");
            Assert.Equal(JsonValueKind.Number, placement.GetProperty("CoordinateDomain").ValueKind);
            Assert.Equal(JsonValueKind.Number, placement.GetProperty("RotationAuthority").ValueKind);
            Assert.Equal(JsonValueKind.Number, placement.GetProperty("GuideMode").ValueKind);
        }
        Assert.True(File.Exists(created.Artifact.AbsolutePath + ".sha256"));
        Assert.True(File.Exists(created.Artifact.AbsolutePath + ".bindings.json"));
    }

    [Fact]
    public async Task Phd2OnlyFineMotionAcceptsExplicitNullIndependentMountTransform()
    {
        Directory.CreateDirectory(root);
        var setup = CreateNightSetup();
        var setupDefinition = await ArtifactIO.WriteJsonAtomicallyAsync(Path.Combine(root, "night-definition.json"), setup, false);
        var locked = await EvidenceBuilders.CreateNightSetupAsync(
            setupDefinition.AbsolutePath, setupDefinition.Sha256, Path.Combine(root, "night-setup.json"), false);

        var phd = CreatePhdEvidence(hash: string.Empty);
        phd = phd with { Sha256 = EvidenceBuilders.ComputePhd2ProfileEvidenceSha256(phd) };
        var phdArtifact = await ArtifactIO.WriteJsonAtomicallyAsync(Path.Combine(root, "phd2.json"), phd, false);
        var slitEvidence = await ArtifactIO.WriteTextAtomicallyAsync(Path.Combine(root, "slit-measurement.txt"), "measured slit geometry", false);
        var definition = CreateDefinition(slitEvidence, phd) with
        {
            FineMotionAuthority = 1,
            MountTransform = null,
        };
        var definitionArtifact = await ArtifactIO.WriteJsonAtomicallyAsync(Path.Combine(root, "commissioning-definition.json"), definition, false);
        var inputs = new CommissioningInputFiles(
            definitionArtifact.AbsolutePath, definitionArtifact.Sha256,
            locked.Artifact.AbsolutePath, locked.Artifact.Sha256,
            phdArtifact.AbsolutePath, phdArtifact.Sha256, phd.Sha256);

        var created = await EvidenceBuilders.CreateCommissioningPresetAsync(
            inputs,
            Path.Combine(root, "preset.json"),
            false,
            verifyCurrentPhd2Registry: false);

        Assert.Equal(1, created.Preset.FineMotionAuthority);
        Assert.Null(created.Preset.MountTransform);
    }

    [Fact]
    public async Task MissingExplicitFocusDomainsPropertyIsRejected()
    {
        Directory.CreateDirectory(root);
        var setup = CreateNightSetup();
        var node = JsonSerializer.SerializeToNode(setup, ArtifactIO.JsonOptions)!.AsObject();
        node.Remove("FocusDomains");
        var artifact = await ArtifactIO.WriteTextAtomicallyAsync(Path.Combine(root, "missing.json"), node.ToJsonString(ArtifactIO.JsonOptions), false);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => EvidenceBuilders.CreateNightSetupAsync(
            artifact.AbsolutePath, artifact.Sha256, Path.Combine(root, "output.json"), false));

        Assert.Contains("FocusDomains", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(root, "output.json")));
    }

    [Fact]
    public async Task MissingExplicitG3SaturationPropertyCannotBeLockedAsNewEvidence()
    {
        Directory.CreateDirectory(root);
        var node = JsonSerializer.SerializeToNode(CreateNightSetup(), ArtifactIO.JsonOptions)!.AsObject();
        node.Remove("G3SaturationAdu");
        var artifact = await ArtifactIO.WriteTextAtomicallyAsync(
            Path.Combine(root, "missing-g3-saturation.json"),
            node.ToJsonString(ArtifactIO.JsonOptions),
            false);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => EvidenceBuilders.CreateNightSetupAsync(
            artifact.AbsolutePath,
            artifact.Sha256,
            Path.Combine(root, "output.json"),
            false));

        Assert.Contains("G3SaturationAdu", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LegacyNightSetupIsReadableButCannotCreateNewCommissioningEvidence()
    {
        Directory.CreateDirectory(root);
        var legacy = CreateNightSetup() with
        {
            SchemaVersion = NightSetupRecord.LegacySchemaVersion,
            TelescopeFocusPositionSteps = 12345,
            FocusDomains = null,
        };
        var legacyNode = JsonSerializer.SerializeToNode(legacy, ArtifactIO.JsonOptions)!.AsObject();
        legacyNode.Remove("FocusDomains");
        var artifact = await ArtifactIO.WriteTextAtomicallyAsync(
            Path.Combine(root, "legacy-night.json"),
            legacyNode.ToJsonString(ArtifactIO.JsonOptions),
            false);

        var validation = await EvidenceBuilders.ValidateNightSetupAsync(artifact.AbsolutePath, artifact.Sha256);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => EvidenceBuilders.CreateNightSetupAsync(
            artifact.AbsolutePath, artifact.Sha256, Path.Combine(root, "locked.json"), false));

        Assert.False(validation.Valid);
        Assert.Equal("1", validation.Bindings["NightSetupSchemaVersion"]);
        Assert.Contains(validation.Issues, issue => issue.Contains("readable for migration", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("three independent focus domains", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(root, "locked.json")));
    }

    [Fact]
    public async Task LegacyFocusDeadlineDoesNotInvalidateOtherwiseUnchangedState()
    {
        Directory.CreateDirectory(root);
        var setup = CreateNightSetup();
        var domains = setup.FocusDomains!;
        var wide = domains.Single(binding => binding.Role == FocusDomainRole.Gs350WideField);
        setup = setup with
        {
            FocusDomains = domains.Select(binding => binding.Role == FocusDomainRole.Gs350WideField
                ? wide with { ValidUntilUtc = DateTimeOffset.UtcNow.AddSeconds(-1) }
                : binding).ToArray(),
        };
        var artifact = await ArtifactIO.WriteJsonAtomicallyAsync(Path.Combine(root, "expired-night.json"), setup, false);

        var validation = await EvidenceBuilders.ValidateNightSetupAsync(artifact.AbsolutePath, artifact.Sha256);

        Assert.True(validation.Valid, string.Join(Environment.NewLine, validation.Issues));
        Assert.DoesNotContain(validation.Issues, issue => issue.Contains("expired", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SeedOrSingularMountSamplesCannotCreateCommissionedPreset()
    {
        Directory.CreateDirectory(root);
        var setupArtifact = await ArtifactIO.WriteJsonAtomicallyAsync(Path.Combine(root, "night.json"), CreateNightSetup(), false);
        var phd = CreatePhdEvidence(hash: string.Empty);
        phd = phd with { Sha256 = EvidenceBuilders.ComputePhd2ProfileEvidenceSha256(phd) };
        var phdArtifact = await ArtifactIO.WriteJsonAtomicallyAsync(Path.Combine(root, "phd.json"), phd, false);
        var slitEvidence = await ArtifactIO.WriteTextAtomicallyAsync(Path.Combine(root, "slit.txt"), "evidence", false);
        var baseDefinition = CreateDefinition(slitEvidence, phd);
        var definition = baseDefinition with
        {
            MountTransform = baseDefinition.MountTransform! with
            {
                Samples =
                [
                    new MountCalibrationSample(10, 0, 5, 0),
                    new MountCalibrationSample(-10, 0, -5, 0),
                    new MountCalibrationSample(20, 0, 10, 0),
                    new MountCalibrationSample(-20, 0, -10, 0),
                ],
            },
        };
        var definitionArtifact = await ArtifactIO.WriteJsonAtomicallyAsync(Path.Combine(root, "definition.json"), definition, false);
        var inputs = new CommissioningInputFiles(
            definitionArtifact.AbsolutePath, definitionArtifact.Sha256,
            setupArtifact.AbsolutePath, setupArtifact.Sha256,
            phdArtifact.AbsolutePath, phdArtifact.Sha256, phd.Sha256);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => EvidenceBuilders.CreateCommissioningPresetAsync(
            inputs, Path.Combine(root, "preset.json"), false, verifyCurrentPhd2Registry: false));

        Assert.Contains("did not pass commissioning", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(root, "preset.json")));
    }

    [Fact]
    public async Task SwappedSlitInstallationCannotBeBlessedByDeclaredOrdinalLabels()
    {
        Directory.CreateDirectory(root);
        var setupArtifact = await ArtifactIO.WriteJsonAtomicallyAsync(Path.Combine(root, "night.json"), CreateNightSetup(), false);
        var phd = CreatePhdEvidence(hash: string.Empty);
        phd = phd with { Sha256 = EvidenceBuilders.ComputePhd2ProfileEvidenceSha256(phd) };
        var phdArtifact = await ArtifactIO.WriteJsonAtomicallyAsync(Path.Combine(root, "phd.json"), phd, false);
        var slitEvidence = await ArtifactIO.WriteTextAtomicallyAsync(Path.Combine(root, "slit.txt"), "evidence", false);
        var definition = CreateDefinition(slitEvidence, phd);
        var fingerprints = definition.SlitWheelIdentity!.Fingerprints.ToArray();
        var position2 = Array.FindIndex(fingerprints, item => item.WheelPosition == 2);
        var position3 = Array.FindIndex(fingerprints, item => item.WheelPosition == 3);
        fingerprints[position2] = fingerprints[position2] with { MeasuredWidthPixels = 15 };
        fingerprints[position3] = fingerprints[position3] with { MeasuredWidthPixels = 9 };
        definition = definition with
        {
            SlitWheelIdentity = definition.SlitWheelIdentity with { Fingerprints = fingerprints },
        };
        var definitionArtifact = await ArtifactIO.WriteJsonAtomicallyAsync(Path.Combine(root, "definition.json"), definition, false);
        var inputs = new CommissioningInputFiles(
            definitionArtifact.AbsolutePath, definitionArtifact.Sha256,
            setupArtifact.AbsolutePath, setupArtifact.Sha256,
            phdArtifact.AbsolutePath, phdArtifact.Sha256, phd.Sha256);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => EvidenceBuilders.CreateCommissioningPresetAsync(
            inputs, Path.Combine(root, "preset.json"), false, verifyCurrentPhd2Registry: false));

        Assert.Contains("Optical slit-width order contradicts", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(root, "preset.json")));
    }

    [Fact]
    public async Task WrongPhd2PolicyHashCannotCreateSchema4Preset()
    {
        Directory.CreateDirectory(root);
        var setupArtifact = await ArtifactIO.WriteJsonAtomicallyAsync(Path.Combine(root, "night.json"), CreateNightSetup(), false);
        var phd = CreatePhdEvidence(hash: string.Empty);
        phd = phd with { Sha256 = EvidenceBuilders.ComputePhd2ProfileEvidenceSha256(phd) };
        var phdArtifact = await ArtifactIO.WriteJsonAtomicallyAsync(Path.Combine(root, "phd.json"), phd, false);
        var slitEvidence = await ArtifactIO.WriteTextAtomicallyAsync(Path.Combine(root, "slit.txt"), "evidence", false);
        var definition = CreateDefinition(slitEvidence, phd);
        definition = definition with
        {
            Phd2SlitPlacement = definition.Phd2SlitPlacement! with
            {
                CalibrationQualityPolicySha256 = new string('0', 64),
            },
        };
        var definitionArtifact = await ArtifactIO.WriteJsonAtomicallyAsync(Path.Combine(root, "definition.json"), definition, false);
        var inputs = new CommissioningInputFiles(
            definitionArtifact.AbsolutePath, definitionArtifact.Sha256,
            setupArtifact.AbsolutePath, setupArtifact.Sha256,
            phdArtifact.AbsolutePath, phdArtifact.Sha256, phd.Sha256);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => EvidenceBuilders.CreateCommissioningPresetAsync(
            inputs, Path.Combine(root, "preset.json"), false, verifyCurrentPhd2Registry: false));

        Assert.Contains("policy SHA-256", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(root, "preset.json")));
    }

    [Fact]
    public async Task WrongPhd2TopologyFingerprintCannotCreateSchema4Preset()
    {
        Directory.CreateDirectory(root);
        var setupArtifact = await ArtifactIO.WriteJsonAtomicallyAsync(Path.Combine(root, "night.json"), CreateNightSetup(), false);
        var phd = CreatePhdEvidence(hash: string.Empty);
        phd = phd with { Sha256 = EvidenceBuilders.ComputePhd2ProfileEvidenceSha256(phd) };
        var phdArtifact = await ArtifactIO.WriteJsonAtomicallyAsync(Path.Combine(root, "phd.json"), phd, false);
        var slitEvidence = await ArtifactIO.WriteTextAtomicallyAsync(Path.Combine(root, "slit.txt"), "evidence", false);
        var definition = CreateDefinition(slitEvidence, phd);
        definition = definition with
        {
            Phd2SlitPlacement = definition.Phd2SlitPlacement! with
            {
                LockedTopologyFingerprintSha256 = new string('F', 64),
            },
        };
        var definitionArtifact = await ArtifactIO.WriteJsonAtomicallyAsync(Path.Combine(root, "definition.json"), definition, false);
        var inputs = new CommissioningInputFiles(
            definitionArtifact.AbsolutePath, definitionArtifact.Sha256,
            setupArtifact.AbsolutePath, setupArtifact.Sha256,
            phdArtifact.AbsolutePath, phdArtifact.Sha256, phd.Sha256);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => EvidenceBuilders.CreateCommissioningPresetAsync(
            inputs, Path.Combine(root, "preset.json"), false, verifyCurrentPhd2Registry: false));

        Assert.Contains("locked topology fingerprint", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(root, "preset.json")));
    }

    [Fact]
    public async Task CompleteGhostCommissioningCreatesAutoSchema4Preset()
    {
        Directory.CreateDirectory(root);
        var setupArtifact = await ArtifactIO.WriteJsonAtomicallyAsync(Path.Combine(root, "night.json"), CreateNightSetup(), false);
        var phd = CreatePhdEvidence(hash: string.Empty);
        phd = phd with { Sha256 = EvidenceBuilders.ComputePhd2ProfileEvidenceSha256(phd) };
        var phdArtifact = await ArtifactIO.WriteJsonAtomicallyAsync(Path.Combine(root, "phd.json"), phd, false);
        var slitEvidence = await ArtifactIO.WriteTextAtomicallyAsync(Path.Combine(root, "slit.txt"), "evidence", false);
        var definition = CreateDefinition(slitEvidence, phd);
        definition = definition with
        {
            GhostAssistanceMode = (int)GhostAssistanceMode.AutoIfValidElseSkip,
            GhostAssistance = CreateGhostAssistance(definition),
        };
        var definitionArtifact = await ArtifactIO.WriteJsonAtomicallyAsync(Path.Combine(root, "definition.json"), definition, false);
        var inputs = new CommissioningInputFiles(
            definitionArtifact.AbsolutePath, definitionArtifact.Sha256,
            setupArtifact.AbsolutePath, setupArtifact.Sha256,
            phdArtifact.AbsolutePath, phdArtifact.Sha256, phd.Sha256);

        var created = await EvidenceBuilders.CreateCommissioningPresetAsync(
            inputs, Path.Combine(root, "preset.json"), false, verifyCurrentPhd2Registry: false);
        var validation = await EvidenceBuilders.ValidateCommissioningPresetAsync(
            inputs, created.Artifact.AbsolutePath, created.Artifact.Sha256, verifyCurrentPhd2Registry: false);

        Assert.True(validation.Valid, string.Join(Environment.NewLine, validation.Issues));
        Assert.NotNull(created.Preset.GhostAssistance);
        Assert.Empty(created.Preset.GhostAssistance!.Validate());
        Assert.Equal((int)GhostAssistanceMode.AutoIfValidElseSkip, created.Bindings.NinaProfileValues["GhostAssistanceMode"]);
        Assert.Equal(false, created.Bindings.NinaProfileValues["ObservationUseRealMode"]);
        Assert.Equal(false, created.Bindings.NinaProfileValues["RealModeCommissioned"]);
        using var json = JsonDocument.Parse(await File.ReadAllBytesAsync(created.Artifact.AbsolutePath));
        Assert.Equal(
            JsonValueKind.Number,
            json.RootElement.GetProperty("GhostAssistance").GetProperty("Calibration").GetProperty("ExtractorKind").ValueKind);
        Assert.Equal(
            JsonValueKind.Number,
            json.RootElement.GetProperty("GhostAssistance").GetProperty("ExtractionPolicy").GetProperty("ExtractorKind").ValueKind);
    }

    [Fact]
    public async Task GhostPolicyHashMismatchCannotCreatePreset()
    {
        Directory.CreateDirectory(root);
        var setupArtifact = await ArtifactIO.WriteJsonAtomicallyAsync(Path.Combine(root, "night.json"), CreateNightSetup(), false);
        var phd = CreatePhdEvidence(hash: string.Empty);
        phd = phd with { Sha256 = EvidenceBuilders.ComputePhd2ProfileEvidenceSha256(phd) };
        var phdArtifact = await ArtifactIO.WriteJsonAtomicallyAsync(Path.Combine(root, "phd.json"), phd, false);
        var slitEvidence = await ArtifactIO.WriteTextAtomicallyAsync(Path.Combine(root, "slit.txt"), "evidence", false);
        var definition = CreateDefinition(slitEvidence, phd);
        var ghost = CreateGhostAssistance(definition) with { MatchPolicySha256 = string.Empty };
        definition = definition with
        {
            GhostAssistanceMode = (int)GhostAssistanceMode.AutoIfValidElseSkip,
            GhostAssistance = ghost,
        };
        var definitionArtifact = await ArtifactIO.WriteJsonAtomicallyAsync(Path.Combine(root, "definition.json"), definition, false);
        var inputs = new CommissioningInputFiles(
            definitionArtifact.AbsolutePath, definitionArtifact.Sha256,
            setupArtifact.AbsolutePath, setupArtifact.Sha256,
            phdArtifact.AbsolutePath, phdArtifact.Sha256, phd.Sha256);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => EvidenceBuilders.CreateCommissioningPresetAsync(
            inputs, Path.Combine(root, "preset.json"), false, verifyCurrentPhd2Registry: false));

        Assert.Contains("match-policy SHA-256", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(root, "preset.json")));
    }

    [Fact]
    public async Task GhostAutoModeWithoutPayloadCannotCreatePreset()
    {
        Directory.CreateDirectory(root);
        var setupArtifact = await ArtifactIO.WriteJsonAtomicallyAsync(Path.Combine(root, "night.json"), CreateNightSetup(), false);
        var phd = CreatePhdEvidence(hash: string.Empty);
        phd = phd with { Sha256 = EvidenceBuilders.ComputePhd2ProfileEvidenceSha256(phd) };
        var phdArtifact = await ArtifactIO.WriteJsonAtomicallyAsync(Path.Combine(root, "phd.json"), phd, false);
        var slitEvidence = await ArtifactIO.WriteTextAtomicallyAsync(Path.Combine(root, "slit.txt"), "evidence", false);
        var definition = CreateDefinition(slitEvidence, phd) with
        {
            GhostAssistanceMode = (int)GhostAssistanceMode.AutoIfValidElseSkip,
            GhostAssistance = null,
        };
        var definitionArtifact = await ArtifactIO.WriteJsonAtomicallyAsync(Path.Combine(root, "definition.json"), definition, false);
        var inputs = new CommissioningInputFiles(
            definitionArtifact.AbsolutePath, definitionArtifact.Sha256,
            setupArtifact.AbsolutePath, setupArtifact.Sha256,
            phdArtifact.AbsolutePath, phdArtifact.Sha256, phd.Sha256);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => EvidenceBuilders.CreateCommissioningPresetAsync(
            inputs, Path.Combine(root, "preset.json"), false, verifyCurrentPhd2Registry: false));

        Assert.Contains("requires a complete hash-bound GhostAssistance payload", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(root, "preset.json")));
    }

    [Fact]
    public async Task ExistingEvidenceIsNotSilentlyOverwritten()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "immutable.json");
        await ArtifactIO.WriteTextAtomicallyAsync(path, "first", false);

        await Assert.ThrowsAsync<IOException>(() => ArtifactIO.WriteTextAtomicallyAsync(path, "second", false));

        Assert.Equal("first", await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.GetFiles(root, "*.tmp", SearchOption.TopDirectoryOnly));
    }

    private static NightSetupRecord CreateNightSetup()
    {
        var atr = new CameraSetup("atr-stable-id", 100, 256, 1, 1, -10, "0", 0, 0, 3840, 2160);
        var qhy = new CameraSetup("qhy-stable-id", 20, 10, 1, 1, -10, "0", 0, 0, 3856, 2180);
        var lockedUtc = DateTimeOffset.UtcNow.AddMinutes(-2);
        return new NightSetupRecord(
            NightSetupRecord.CurrentSchemaVersion, "night-setup-1", lockedUtc,
            4, 35, -1923, 559.15, 12000, 45000,
            atr, "g3-stable-id", "profile-name", qhy,
            DispersionDirection.BlueAtLeftRedAtRight, 382.8, 735.5,
            CalibrationStrategy.CompactEmissionLineObject, "NGC 6543",
            new HorizonPolicy(40, 5, 2, TimeSpan.FromMinutes(2), null),
            "connected safety monitor and explicit roof state", false, 6800,
            CreateFocusDomains(lockedUtc, atr.StableDeviceId, "g3-stable-id", qhy.StableDeviceId));
    }

    private static IReadOnlyList<FocusDomainBinding> CreateFocusDomains(
        DateTimeOffset lockedUtc,
        string atrId,
        string g3Id,
        string qhyId) =>
    [
        new(
            FocusDomainRole.C11Main,
            FocusDomainConventions.C11Owner,
            FocusDomainConventions.C11LogicalDeviceId,
            new FocusPhysicalBinding(FocusMechanism.Gemini, FocusDomainConventions.C11ConnectionEndpoint, @"USB\VID_1A86&PID_7523\C11-GEMINI-001", null),
            45000,
            new FocusMotionLimits(0, 100000, 200, 1000, FocusApproachDirection.IncreasingSteps, 50),
            new FocusMetricEvidence(FocusMetricKind.G3StellarShape, g3Id, 2.3, "FWHM pixels", new string('1', 64)),
            lockedUtc.AddMinutes(-10), lockedUtc.AddDays(2), 0.95),
        new(
            FocusDomainRole.Gs350WideField,
            "ManualOperator",
            FocusDomainConventions.Gs350LogicalDeviceId,
            new FocusPhysicalBinding(FocusMechanism.ToupTekAaf, FocusDomainConventions.Gs350ConnectionEndpoint, @"USB\VID_0547&PID_14AD\GS350-AAF-001", "PCIROOT(0)#USBROOT(0)#USB(3)"),
            18000,
            new FocusMotionLimits(0, 50000, 0, 0, FocusApproachDirection.None, 0),
            new FocusMetricEvidence(FocusMetricKind.QhyStellarShapeAndPlateSolve, qhyId, 2.1, "FWHM pixels", new string('2', 64)),
            lockedUtc.AddMinutes(-8), lockedUtc.AddDays(2), 0.93),
        new(
            FocusDomainRole.UvexSpectral,
            FocusDomainConventions.UvexOwner,
            FocusDomainConventions.UvexLogicalDeviceId,
            new FocusPhysicalBinding(FocusMechanism.UvexM2, FocusDomainConventions.UvexConnectionEndpoint, @"USB\VID_1A86&PID_7523\UVEX4-COM5-001", null),
            12000,
            new FocusMotionLimits(-50000, 50000, 100, 500, FocusApproachDirection.IncreasingSteps, 25),
            new FocusMetricEvidence(FocusMetricKind.AtrSpectralLineWidth, atrId, 2.7, "FWHM pixels", new string('3', 64)),
            lockedUtc.AddMinutes(-6), lockedUtc.AddDays(2), 0.97),
    ];

    private static Phd2ProfileBindingSnapshot CreatePhdEvidence(string hash) => new(
        2, "profile-name", "ToupTek Camera", ["g3-stable-id"], "OnStep Telescope (ASCOM)",
        1, 100, 2150, 16, @"HKCU\Software\StarkLabs\PHDGuidingV2\profile\2", hash, DateTimeOffset.UtcNow.AddMinutes(-1));

    private static CommissioningMeasurementDefinition CreateDefinition(
        WrittenArtifact slitEvidence,
        Phd2ProfileBindingSnapshot phdEvidence)
    {
        var now = DateTimeOffset.UtcNow;
        var placement = CreatePhd2SlitPlacement(phdEvidence);
        var identityMeasurements = CreateSlitIdentityMeasurements(slitEvidence, now);
        return new CommissioningMeasurementDefinition(
            CommissioningMeasurementDefinition.CurrentSchemaVersion,
            "preset-1", now, now.AddDays(1), "bounded commissioning measurement set",
            now.AddMinutes(-5).ToString("O"), "ASCOM.OnStep.Telescope", "g3-stable-id",
            1, 1000, 100, false,
            new SlitMeasurementDefinition(
                "slit-cal-1", now.AddMinutes(-10), slitEvidence.AbsolutePath, slitEvidence.Sha256,
                937, 440, 3, 1000, 3, 0.5),
            new MountMeasurementDefinition(
                "mount-cal-1", now.AddMinutes(-8), "East", 1.5, 20, 20,
                [
                    new MountCalibrationSample(10, 0, 0, 5),
                    new MountCalibrationSample(0, 10, -5, 0),
                    new MountCalibrationSample(-10, 0, 0, -5),
                    new MountCalibrationSample(0, -10, 5, 0),
                ]),
            new MotionLimitContract(30, 120, 5, 12),
            new EnvironmentContract(true, true, true, 50, 90, 12),
            4095,
            2,
            placement,
            (int)GhostAssistanceMode.Skip,
            GhostAssistance: null,
            SlitWheelIdentity: new SlitWheelIdentityMeasurementDefinition(
                "slit-wheel-identity-1",
                placement.InstallationEpochId,
                1920,
                1080,
                3,
                2,
                identityMeasurements));
    }

    private static IReadOnlyList<SlitWidthFingerprintMeasurementDefinition> CreateSlitIdentityMeasurements(
        WrittenArtifact slitEvidence,
        DateTimeOffset now)
    {
        var directory = Path.GetDirectoryName(slitEvidence.AbsolutePath)!;
        var definitions = new (int Position, string Label, double Micrometers, double Pixels, double Uncertainty)[]
        {
            (1, "300um", 300, 70, 1.0),
            (2, "15um", 15, 9, 0.6),
            (3, "25um", 25, 15, 0.7),
            (4, "35um", 35, 22, 0.8),
        };
        return definitions.Select(item =>
        {
            var path = Path.Combine(directory, $"slit-identity-position-{item.Position}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                item.Position,
                item.Label,
                item.Micrometers,
                item.Pixels,
                item.Uncertainty,
            }));
            var sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            var shortPath = Path.Combine(directory, $"slit-identity-position-{item.Position}-10ms.json");
            var longPath = Path.Combine(directory, $"slit-identity-position-{item.Position}-20ms.json");
            File.WriteAllText(shortPath, JsonSerializer.Serialize(new { item.Position, ExposureMilliseconds = 10, Role = "HDR-short" }));
            File.WriteAllText(longPath, JsonSerializer.Serialize(new { item.Position, ExposureMilliseconds = 20, Role = "HDR-long" }));
            var shortSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(shortPath)));
            var longSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(longPath)));
            return new SlitWidthFingerprintMeasurementDefinition(
                item.Position,
                item.Label,
                item.Micrometers,
                item.Pixels,
                item.Uncertainty,
                now.AddMinutes(-12 + item.Position),
                path,
                sha256,
                (int)SlitDarkApertureResolution.DirectTwoEdge,
                item.Pixels / 2,
                0.1,
                shortPath,
                shortSha256,
                longPath,
                longSha256);
        }).ToArray();
    }

    private static Phd2SlitPlacementContract CreatePhd2SlitPlacement(Phd2ProfileBindingSnapshot phdEvidence)
    {
        var policy = Phd2CalibrationQualityPolicy.Default;
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
                phdEvidence,
                "g3-stable-id",
                1),
        };
    }

    private static GhostAssistanceContract CreateGhostAssistance(CommissioningMeasurementDefinition definition)
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
        var extractionSha256 = extraction.ComputeContentSha256();
        var calibration = new GhostTemplateCalibration(
            GhostTemplateCalibration.CurrentSchemaVersion,
            "ghost-calibration-1",
            definition.Phd2SlitPlacement!.InstallationEpochId,
            definition.G3CameraStableId,
            "2",
            extraction.ExtractorKind,
            extraction.ExtractorVersion,
            extraction.PolicyId,
            extractionSha256,
            new string('4', 64),
            new GhostDetectorGeometry(0, 0, 1920, 1080, 1, 1),
            new string('5', 64),
            12.5,
            "East",
            definition.G3GainPercent,
            500,
            2_000,
            definition.CreatedUtc.AddHours(-1),
            definition.ValidUntilUtc!.Value.AddDays(1),
            1,
            2,
            new GhostCovariance2D(1, 0, 1),
            [
                new GhostTemplateFeature("ghost-a", new PixelPoint(20, 10), 0.2, new GhostCovariance2D(1, 0, 1)),
                new GhostTemplateFeature("ghost-b", new PixelPoint(-15, 8), 0.1, new GhostCovariance2D(1, 0, 1)),
            ],
            new string('6', 64),
            string.Empty).WithComputedSha256();
        var policy = new GhostTemplatePolicy(
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
            policy,
            GhostAssistanceContract.ComputeContentSha256(policy),
            extraction,
            extractionSha256,
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

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
