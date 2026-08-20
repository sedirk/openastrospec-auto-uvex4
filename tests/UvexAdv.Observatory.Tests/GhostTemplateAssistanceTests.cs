using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Observatory.Tests;

public sealed class GhostTemplateAssistanceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 19, 1, 0, 0, TimeSpan.Zero);
    private static readonly string ShaA = new('A', 64);
    private static readonly string ShaB = new('B', 64);
    private static readonly string ShaC = new('C', 64);
    private static readonly string ShaD = new('D', 64);

    [Fact]
    public void CorrectTemplateProducesAuxiliaryCentroidButNeverIdentityOrMotionAuthority()
    {
        var result = Evaluate();

        Assert.Equal(GhostAssistanceDecision.UseCalibratedAuxiliaryEstimate, result.Decision);
        Assert.Equal(GhostLocatorAuthority.CalibratedAuxiliaryOnly, result.Authority);
        Assert.Equal(GateDisposition.Passed, result.Gate.Disposition);
        Assert.Equal("GHOST_AUXILIARY_TARGET_ESTIMATE_VALID", result.Gate.Code);
        Assert.NotNull(result.EstimatedTargetCentroid);
        Assert.InRange(result.EstimatedTargetCentroid!.X, 201.95, 202.05);
        Assert.InRange(result.EstimatedTargetCentroid.Y, 148.95, 149.05);
        Assert.InRange(result.TargetUncertaintyPixels, 0, Policy().MaximumTargetUncertaintyPixels);
        Assert.Equal(2, result.FrameMatches.Count);
        Assert.All(result.FrameMatches, match => Assert.Equal(3, match.Features.Count));
        Assert.False(result.CanEstablishTargetIdentity);
        Assert.Contains("not mount authority", result.Gate.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExplicitSkipDoesNotInspectOrRequireCalibrationAndSelectsDeterministicFallback()
    {
        var result = GhostTemplateAssistance.Evaluate(
            GhostAssistanceMode.Skip,
            null,
            Policy() with { SchemaVersion = -1 },
            Binding(),
            Array.Empty<GhostFrameObservation>());

        Assert.Equal(GhostAssistanceDecision.ContinueLongExposureWcsFallback, result.Decision);
        Assert.Equal(GateDisposition.Passed, result.Gate.Disposition);
        Assert.Equal("GHOST_ASSISTANCE_SKIPPED_FALLBACK", result.Gate.Code);
        Assert.Equal(GhostLocatorAuthority.None, result.Authority);
        Assert.Null(result.EstimatedTargetCentroid);
        Assert.Contains("long-exposure WCS", result.Gate.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AutoWithoutCalibrationFallsBackWhileRequireValidPauses()
    {
        var auto = GhostTemplateAssistance.Evaluate(
            GhostAssistanceMode.AutoIfValidElseSkip,
            null,
            Policy(),
            Binding(),
            Frames());
        var required = GhostTemplateAssistance.Evaluate(
            GhostAssistanceMode.RequireValid,
            null,
            Policy(),
            Binding(),
            Frames());

        Assert.Equal(GhostAssistanceDecision.ContinueLongExposureWcsFallback, auto.Decision);
        Assert.Equal(GateDisposition.Passed, auto.Gate.Disposition);
        Assert.Equal("GHOST_CALIBRATION_MISSING", auto.TemplateGate.Code);
        Assert.Equal(GhostAssistanceDecision.PauseNeedsAttention, required.Decision);
        Assert.Equal(GateDisposition.Indeterminate, required.Gate.Disposition);
        Assert.Equal("GHOST_REQUIRED_ASSISTANCE_UNAVAILABLE", required.Gate.Code);
        Assert.DoesNotContain("authorized", required.Gate.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("old")]
    [InlineData("rotated")]
    [InlineData("reinstalled")]
    [InlineData("roi")]
    [InlineData("pier")]
    [InlineData("topology")]
    [InlineData("extractor")]
    public void ChangedInstallationBindingsInvalidateTemplateAndFallBack(string mutation)
    {
        var calibration = Calibration();
        var binding = Binding();
        switch (mutation)
        {
            case "old":
                calibration = (calibration with
                {
                    CreatedUtc = Now.AddDays(-20),
                    ValidUntilUtc = Now.AddDays(1),
                }).WithComputedSha256();
                break;
            case "rotated":
                binding = binding with { OrientationDegrees = binding.OrientationDegrees + 3 };
                break;
            case "reinstalled":
                binding = binding with { InstallationEpochId = "INSTALL-EPOCH-2" };
                break;
            case "roi":
                binding = binding with
                {
                    Detector = binding.Detector with { RoiX = binding.Detector.RoiX + 2 },
                };
                break;
            case "pier":
                binding = binding with { PierSide = "West" };
                break;
            case "topology":
                binding = binding with { OpticalTopologySha256 = ShaD };
                break;
            case "extractor":
                binding = binding with { ExtractionPolicySha256 = ShaD };
                break;
        }

        var result = Evaluate(calibration: calibration, binding: binding);

        Assert.Equal(GhostAssistanceDecision.ContinueLongExposureWcsFallback, result.Decision);
        Assert.Equal(GhostLocatorAuthority.None, result.Authority);
        Assert.Equal("GHOST_TEMPLATE_NOT_APPLICABLE", result.TemplateGate.Code);
        Assert.Null(result.EstimatedTargetCentroid);
    }

    [Fact]
    public void OrientationFingerprintMismatchInvalidatesEvenWhenNumericAngleWasCopied()
    {
        var result = Evaluate(binding: Binding() with { OrientationFingerprintSha256 = ShaD });

        Assert.Equal(GhostAssistanceDecision.ContinueLongExposureWcsFallback, result.Decision);
        Assert.Contains("orientation fingerprint changed", result.TemplateGate.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DuplicateNeighbourPatternIsRejectedAsAmbiguous()
    {
        var frames = Frames()
            .Select((frame, index) => frame with
            {
                Detections = frame.Detections.Concat(PatternDetections(
                    new PixelPoint(280 + 2 * index, 210 - index),
                    frame.ExposureMilliseconds,
                    $"neighbour-{index}")).ToArray(),
            })
            .ToArray();

        var result = Evaluate(frames: frames);

        Assert.Equal(GhostAssistanceDecision.ContinueLongExposureWcsFallback, result.Decision);
        Assert.Equal("GHOST_PATTERN_AMBIGUOUS", result.TemplateGate.Code);
        Assert.Null(result.EstimatedTargetCentroid);
    }

    [Fact]
    public void WrongRelativeBrightnessPatternCannotMasqueradeAsGhost()
    {
        var frames = Frames()
            .Select(frame => frame with
            {
                Detections = frame.Detections.Select(detection =>
                    detection.DetectionId.EndsWith("-b", StringComparison.Ordinal)
                        ? detection with { IntegratedFluxAdu = detection.IntegratedFluxAdu * 8 }
                        : detection).ToArray(),
            })
            .ToArray();

        var result = Evaluate(frames: frames);

        Assert.Equal(GhostAssistanceDecision.ContinueLongExposureWcsFallback, result.Decision);
        Assert.Equal("GHOST_PATTERN_NOT_FOUND", result.TemplateGate.Code);
    }

    [Fact]
    public void ExposureNormalizedBrightnessMustRemainConsistentAcrossFrames()
    {
        var frames = Frames();
        frames[1] = frames[1] with
        {
            Detections = frames[1].Detections
                .Select(detection => detection with { IntegratedFluxAdu = detection.IntegratedFluxAdu * 0.35 })
                .ToArray(),
        };

        var result = Evaluate(frames: frames);

        Assert.Equal(GhostAssistanceDecision.ContinueLongExposureWcsFallback, result.Decision);
        Assert.Equal("GHOST_MULTIFRAME_INCONSISTENT", result.TemplateGate.Code);
        Assert.Contains("exposure-normalized flux", result.TemplateGate.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FeatureThatDoesNotShareCommandedMotionRejectsWholeSequence()
    {
        var frames = Frames();
        frames[1] = frames[1] with
        {
            Detections = frames[1].Detections.Select(detection =>
                detection.DetectionId.EndsWith("-c", StringComparison.Ordinal)
                    ? detection with
                    {
                        Centroid = new PixelPoint(detection.Centroid.X + 0.8, detection.Centroid.Y - 0.5),
                    }
                    : detection).ToArray(),
        };

        var result = Evaluate(policy: Policy() with { MaximumCommonMotionResidualPixels = 0.2 }, frames: frames);

        Assert.Equal(GhostAssistanceDecision.ContinueLongExposureWcsFallback, result.Decision);
        Assert.Equal("GHOST_MULTIFRAME_INCONSISTENT", result.TemplateGate.Code);
        Assert.Contains("common-motion", result.TemplateGate.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExcessiveCalibrationCovarianceWithholdsCentroid()
    {
        var calibration = (Calibration() with
        {
            TargetSystematicCovariancePixelsSquared = new GhostCovariance2D(4, 0.2, 3),
        }).WithComputedSha256();

        var result = Evaluate(calibration: calibration);

        Assert.Equal(GhostAssistanceDecision.ContinueLongExposureWcsFallback, result.Decision);
        Assert.Equal("GHOST_TARGET_UNCERTAINTY_HIGH", result.TemplateGate.Code);
        Assert.Null(result.EstimatedTargetCentroid);
    }

    [Fact]
    public void CalibrationRmsIsSystematicAndCannotBeAveragedAwayByManyGhosts()
    {
        var calibration = (Calibration() with
        {
            CalibrationRmsResidualPixels = 1.2,
            CalibrationMaximumResidualPixels = 1.3,
            TargetSystematicCovariancePixelsSquared = new GhostCovariance2D(0.001, 0, 0.001),
            Features = Calibration().Features.Select(feature => feature with
            {
                OffsetCovariancePixelsSquared = new GhostCovariance2D(0.001, 0, 0.001),
            }).ToArray(),
        }).WithComputedSha256();

        var result = Evaluate(calibration: calibration);

        Assert.Equal(GhostAssistanceDecision.ContinueLongExposureWcsFallback, result.Decision);
        Assert.Equal("GHOST_TARGET_UNCERTAINTY_HIGH", result.TemplateGate.Code);
    }

    [Fact]
    public void EdgeClippedFeatureIsNotEligible()
    {
        var frames = Frames();
        frames[0] = frames[0] with
        {
            Detections = frames[0].Detections.Select(detection =>
                detection.DetectionId.EndsWith("-a", StringComparison.Ordinal)
                    ? detection with { EdgeDistancePixels = 2 }
                    : detection).ToArray(),
        };

        var result = Evaluate(frames: frames);

        Assert.Equal(GhostAssistanceDecision.ContinueLongExposureWcsFallback, result.Decision);
        Assert.Equal("GHOST_DETECTIONS_INSUFFICIENT", result.TemplateGate.Code);
    }

    [Fact]
    public void GhostNeverReplacesFreshExternalCatalogueIdentity()
    {
        var binding = Binding();
        binding = binding with
        {
            ExternalIdentity = binding.ExternalIdentity with
            {
                Gate = GateResult.Unknown("WCS_UNKNOWN", "No WCS target identity."),
            },
        };

        var result = Evaluate(binding: binding);

        Assert.Equal(GhostAssistanceDecision.ContinueLongExposureWcsFallback, result.Decision);
        Assert.Equal("GHOST_TEMPLATE_NOT_APPLICABLE", result.TemplateGate.Code);
        Assert.Contains("external catalogue/WCS", result.TemplateGate.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CalibrationContentMutationWithoutNewHashIsRejected()
    {
        var calibration = Calibration();
        calibration = calibration with { PierSide = "West" };

        var result = Evaluate(calibration: calibration, binding: Binding() with { PierSide = "West" });

        Assert.Equal(GhostAssistanceDecision.ContinueLongExposureWcsFallback, result.Decision);
        Assert.Equal("GHOST_CALIBRATION_INVALID", result.TemplateGate.Code);
        Assert.Contains("content SHA-256", result.TemplateGate.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InMemoryG3FramesFlowThroughDeterministicSourceAdapterWithoutHandAuthoredDetections()
    {
        var extraction = ExtractionPolicy();
        var first = GhostFrameObservationFactory.FromMonochromeFrame(
            SyntheticFrame(new PixelPoint(200, 150), amplitudeScale: 1),
            new GhostFrameCaptureMetadata("raw-1", ShaA, Now.AddSeconds(-50), 1_000, 100, new PixelPoint(0, 0)),
            extraction);
        var second = GhostFrameObservationFactory.FromMonochromeFrame(
            SyntheticFrame(new PixelPoint(202, 149), amplitudeScale: 2),
            new GhostFrameCaptureMetadata("raw-2", ShaB, Now.AddSeconds(-20), 2_000, 100, new PixelPoint(2, -1), ShaC),
            extraction);

        Assert.Equal(GateDisposition.Passed, first.Gate.Disposition);
        Assert.Equal(GateDisposition.Passed, second.Gate.Disposition);
        Assert.NotNull(first.Observation);
        Assert.NotNull(second.Observation);
        Assert.True(GhostTemplateCalibration.IsSha256(first.EvidenceSha256));
        Assert.Equal(3, first.OverlaySources.Count);
        Assert.All(first.OverlaySources, overlay => Assert.Equal("ghost-candidate", overlay.Label));

        var result = Evaluate(frames: new[] { first.Observation!, second.Observation! });

        Assert.Equal(GhostAssistanceDecision.UseCalibratedAuxiliaryEstimate, result.Decision);
        Assert.InRange(result.EstimatedTargetCentroid!.X, 201.95, 202.05);
        Assert.InRange(result.EstimatedTargetCentroid.Y, 148.95, 149.05);
    }

    [Fact]
    public void SourceAdapterRejectsUnhashedFrameBeforeReturningAnyObservation()
    {
        var result = GhostFrameObservationFactory.FromMonochromeFrame(
            SyntheticFrame(new PixelPoint(200, 150), amplitudeScale: 1),
            new GhostFrameCaptureMetadata("raw-unhashed", string.Empty, Now, 1_000, 100),
            ExtractionPolicy());

        Assert.Equal(GateDisposition.Indeterminate, result.Gate.Disposition);
        Assert.Equal("GHOST_SOURCE_FRAME_METADATA_INVALID", result.Gate.Code);
        Assert.Null(result.Observation);
        Assert.Empty(result.OverlaySources);
    }

    private static GhostAssistanceResult Evaluate(
        GhostAssistanceMode mode = GhostAssistanceMode.AutoIfValidElseSkip,
        GhostTemplateCalibration? calibration = default,
        GhostTemplatePolicy? policy = null,
        GhostRuntimeBinding? binding = null,
        IReadOnlyList<GhostFrameObservation>? frames = null)
    {
        return GhostTemplateAssistance.Evaluate(
            mode,
            calibration ?? Calibration(),
            policy ?? Policy(),
            binding ?? Binding(),
            frames ?? Frames());
    }

    private static GhostTemplateCalibration Calibration()
    {
        var calibration = new GhostTemplateCalibration(
            GhostTemplateCalibration.CurrentSchemaVersion,
            "GHOST-CAL-001",
            "INSTALL-EPOCH-1",
            "G3-STABLE-ID",
            "PHD2-PROFILE-7",
            GhostFeatureExtractorKind.PointSourceStarFieldV1,
            GhostSourceExtractionPolicy.CurrentBackendVersion,
            ExtractionPolicy().PolicyId,
            ExtractionPolicy().ComputeContentSha256(),
            ShaA,
            new GhostDetectorGeometry(0, 0, 400, 300, 1, 1),
            ShaB,
            12.5,
            "East",
            100,
            500,
            3_000,
            Now.AddDays(-2),
            Now.AddDays(30),
            0.2,
            0.5,
            new GhostCovariance2D(0.04, 0.005, 0.05),
            new[]
            {
                new GhostTemplateFeature("a", new PixelPoint(30, 0), 1.0, new GhostCovariance2D(0.04, 0, 0.04)),
                new GhostTemplateFeature("b", new PixelPoint(-20, 15), 0.5, new GhostCovariance2D(0.04, 0, 0.04)),
                new GhostTemplateFeature("c", new PixelPoint(5, -25), 0.25, new GhostCovariance2D(0.04, 0, 0.04)),
            },
            ShaC,
            string.Empty);
        return calibration.WithComputedSha256();
    }

    private static GhostTemplatePolicy Policy() => new(
        GhostTemplatePolicy.CurrentSchemaVersion,
        "GHOST-POLICY-001",
        TimeSpan.FromDays(7),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(2),
        0.5,
        2,
        3,
        1.5,
        0.2,
        0.15,
        0.35,
        0.4,
        4,
        2,
        10,
        1.0);

    private static GhostSourceExtractionPolicy ExtractionPolicy() => new(
        GhostSourceExtractionPolicy.CurrentSchemaVersion,
        "GHOST-EXTRACT-001",
        GhostFeatureExtractorKind.PointSourceStarFieldV1,
        GhostSourceExtractionPolicy.CurrentBackendVersion,
        new StarDetectionOptions(
            DetectionSigma: 5,
            CentroidRadiusPixels: 3,
            EdgeMarginPixels: 10,
            MaximumCandidates: 50,
            MaximumEllipticity: 0.5,
            MaximumSaturatedFraction: 0),
        MinimumSignalToNoise: 5,
        MaximumGhostEllipticity: 0.4,
        MinimumCentroidSigmaPixels: 0.02,
        MaximumCentroidSigmaPixels: 1);

    private static GhostRuntimeBinding Binding() => new(
        "RUN-001",
        "HIP-001",
        Now,
        "INSTALL-EPOCH-1",
        "G3-STABLE-ID",
        "PHD2-PROFILE-7",
        GhostFeatureExtractorKind.PointSourceStarFieldV1,
        GhostSourceExtractionPolicy.CurrentBackendVersion,
        ExtractionPolicy().PolicyId,
        ExtractionPolicy().ComputeContentSha256(),
        ShaA,
        new GhostDetectorGeometry(0, 0, 400, 300, 1, 1),
        ShaB,
        12.5,
        "East",
        new GhostExternalIdentityEvidence(
            "RUN-001",
            "HIP-001",
            GhostExternalIdentityAuthority.CatalogBoundQhyWcs,
            GateResult.Pass("FRESH_WCS_IDENTITY", "Fresh catalog-bound WCS identity."),
            ShaD,
            Now.AddMinutes(-2),
            Now.AddMinutes(5)));

    private static GhostFrameObservation[] Frames() =>
    [
        new GhostFrameObservation(
            "frame-1",
            ShaA,
            Now.AddSeconds(-50),
            400,
            300,
            1_000,
            100,
            PatternDetections(new PixelPoint(200, 150), 1_000, "f1"),
            GhostFeatureExtractorKind.PointSourceStarFieldV1,
            GhostSourceExtractionPolicy.CurrentBackendVersion,
            ExtractionPolicy().PolicyId,
            ExtractionPolicy().ComputeContentSha256(),
            ShaD,
            new PixelPoint(0, 0)),
        new GhostFrameObservation(
            "frame-2",
            ShaB,
            Now.AddSeconds(-20),
            400,
            300,
            2_000,
            100,
            PatternDetections(new PixelPoint(202, 149), 2_000, "f2"),
            GhostFeatureExtractorKind.PointSourceStarFieldV1,
            GhostSourceExtractionPolicy.CurrentBackendVersion,
            ExtractionPolicy().PolicyId,
            ExtractionPolicy().ComputeContentSha256(),
            ShaD,
            new PixelPoint(2, -1),
            ShaC),
    ];

    private static GhostSourceDetection[] PatternDetections(
        PixelPoint target,
        int exposureMilliseconds,
        string prefix) =>
    [
        Detection($"{prefix}-a", target.X + 30, target.Y, 10 * exposureMilliseconds),
        Detection($"{prefix}-b", target.X - 20, target.Y + 15, 5 * exposureMilliseconds),
        Detection($"{prefix}-c", target.X + 5, target.Y - 25, 2.5 * exposureMilliseconds),
    ];

    private static GhostSourceDetection Detection(string id, double x, double y, double flux) =>
        new(id, new PixelPoint(x, y), flux, 0.1, 40);

    private static MonochromeFrame SyntheticFrame(PixelPoint target, double amplitudeScale)
    {
        const int width = 400;
        const int height = 300;
        var pixels = Enumerable.Repeat((ushort)100, width * height).ToArray();
        AddPointSource(pixels, width, height, target.X + 30, target.Y, 1_200 * amplitudeScale);
        AddPointSource(pixels, width, height, target.X - 20, target.Y + 15, 600 * amplitudeScale);
        AddPointSource(pixels, width, height, target.X + 5, target.Y - 25, 300 * amplitudeScale);
        return new MonochromeFrame(width, height, pixels, 60_000);
    }

    private static void AddPointSource(
        ushort[] pixels,
        int width,
        int height,
        double x,
        double y,
        double amplitude)
    {
        var centerX = (int)Math.Round(x);
        var centerY = (int)Math.Round(y);
        for (var dy = -2; dy <= 2; dy++)
        for (var dx = -2; dx <= 2; dx++)
        {
            var px = centerX + dx;
            var py = centerY + dy;
            if (px < 0 || py < 0 || px >= width || py >= height) continue;
            var radiusSquared = dx * dx + dy * dy;
            var signal = amplitude * Math.Exp(-radiusSquared / 2d);
            pixels[py * width + px] = (ushort)Math.Round(100 + signal);
        }
    }
}
