using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UvexAdv.Observatory;

/// <summary>
/// Controls whether an installation-specific optical-ghost template may be
/// consulted.  Skip and Auto never turn a missing/invalid template into an
/// acquisition failure; RequireValid is the explicit expert-only exception.
/// </summary>
public enum GhostAssistanceMode
{
    Skip,
    AutoIfValidElseSkip,
    RequireValid,
}

public enum GhostAssistanceDecision
{
    ContinueLongExposureWcsFallback,
    UseCalibratedAuxiliaryEstimate,
    PauseNeedsAttention,
}

/// <summary>
/// A passing ghost match is deliberately auxiliary.  It never establishes
/// catalogue identity and never authorizes mount motion by itself.
/// </summary>
public enum GhostLocatorAuthority
{
    None,
    CalibratedAuxiliaryOnly,
}

/// <summary>
/// Current ordinary-program backend. It is intentionally limited to compact,
/// point-like ghosts detected by StarFieldDetector. Ring/arc/extended ghosts
/// require a separately versioned backend and cannot reuse this calibration.
/// </summary>
public enum GhostFeatureExtractorKind
{
    PointSourceStarFieldV1,
}

public enum GhostExternalIdentityAuthority
{
    CatalogBoundQhyWcs,
    CatalogBoundG3Wcs,
    CatalogBoundTemporalContinuity,
}

/// <summary>
/// ROI coordinates and dimensions are unbinned sensor pixels. Template
/// vectors and runtime centroids are delivered-image pixels after binning.
/// </summary>
public sealed record GhostDetectorGeometry(
    int RoiX,
    int RoiY,
    int RoiWidth,
    int RoiHeight,
    int BinningX,
    int BinningY)
{
    public IReadOnlyList<string> Validate()
    {
        var issues = new List<string>();
        if (RoiX < 0 || RoiY < 0) issues.Add("ghost detector ROI origin must be non-negative");
        if (RoiWidth <= 0 || RoiHeight <= 0) issues.Add("ghost detector ROI dimensions must be positive");
        if (BinningX <= 0 || BinningY <= 0) issues.Add("ghost detector binning must be positive");
        if (RoiWidth > 0 && BinningX > 0 && RoiWidth % BinningX != 0)
            issues.Add("ghost detector ROI width must be divisible by X binning");
        if (RoiHeight > 0 && BinningY > 0 && RoiHeight % BinningY != 0)
            issues.Add("ghost detector ROI height must be divisible by Y binning");
        return issues.AsReadOnly();
    }

    public int OutputWidth => BinningX > 0 ? RoiWidth / BinningX : 0;
    public int OutputHeight => BinningY > 0 ? RoiHeight / BinningY : 0;
}

/// <summary>
/// Symmetric covariance in delivered-image pixel squared units.
/// </summary>
public sealed record GhostCovariance2D(double XX, double XY, double YY)
{
    public bool IsPositiveSemidefinite =>
        double.IsFinite(XX) && double.IsFinite(XY) && double.IsFinite(YY) &&
        XX >= 0 && YY >= 0 && XX * YY - XY * XY >= -1e-12;

    public double MaximumSigmaPixels
    {
        get
        {
            if (!IsPositiveSemidefinite) return double.PositiveInfinity;
            var discriminant = Math.Sqrt(Math.Max(0, (XX - YY) * (XX - YY) + 4 * XY * XY));
            return Math.Sqrt(Math.Max(0, 0.5 * (XX + YY + discriminant)));
        }
    }
}

/// <summary>
/// One reproducible optical-ghost feature. OffsetFromTarget is an optical
/// relationship measured for the exact installation fingerprint, not a target
/// coordinate or a QHY-to-C11 boresight offset.
/// </summary>
public sealed record GhostTemplateFeature(
    string FeatureId,
    PixelPoint OffsetFromTarget,
    double RelativeFlux,
    GhostCovariance2D OffsetCovariancePixelsSquared);

/// <summary>
/// Immutable, versioned commissioning output for an optical ghost pattern.
/// No present-night target name, target coordinate, or fixed two-telescope
/// optical-axis displacement belongs in this record.
/// </summary>
public sealed record GhostTemplateCalibration(
    int SchemaVersion,
    string CalibrationId,
    string InstallationEpochId,
    string CameraStableId,
    string Phd2ProfileId,
    GhostFeatureExtractorKind ExtractorKind,
    int ExtractorVersion,
    string ExtractionPolicyId,
    string ExtractionPolicySha256,
    string OpticalTopologySha256,
    GhostDetectorGeometry Detector,
    string OrientationFingerprintSha256,
    double OrientationDegrees,
    string PierSide,
    int Gain,
    int MinimumExposureMilliseconds,
    int MaximumExposureMilliseconds,
    DateTimeOffset CreatedUtc,
    DateTimeOffset ValidUntilUtc,
    double CalibrationRmsResidualPixels,
    double CalibrationMaximumResidualPixels,
    GhostCovariance2D TargetSystematicCovariancePixelsSquared,
    IReadOnlyList<GhostTemplateFeature> Features,
    string CalibrationEvidenceSha256,
    string CalibrationSha256)
{
    public const int CurrentSchemaVersion = 1;

    public GhostTemplateCalibration WithComputedSha256() =>
        this with { CalibrationSha256 = ComputeContentSha256(this) };

    public bool HasValidContentSha256() =>
        IsSha256(CalibrationSha256) &&
        string.Equals(CalibrationSha256, ComputeContentSha256(this), StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<string> Validate()
    {
        var issues = new List<string>();
        if (SchemaVersion != CurrentSchemaVersion)
            issues.Add($"ghost calibration schema must be {CurrentSchemaVersion}");
        if (string.IsNullOrWhiteSpace(CalibrationId)) issues.Add("ghost calibration ID is missing");
        if (string.IsNullOrWhiteSpace(InstallationEpochId)) issues.Add("ghost installation epoch is missing");
        if (string.IsNullOrWhiteSpace(CameraStableId)) issues.Add("ghost camera stable ID is missing");
        if (string.IsNullOrWhiteSpace(Phd2ProfileId)) issues.Add("ghost PHD2 profile ID is missing");
        if (ExtractorKind != GhostFeatureExtractorKind.PointSourceStarFieldV1)
            issues.Add("ghost feature extractor kind is unsupported");
        if (ExtractorVersion != GhostSourceExtractionPolicy.CurrentBackendVersion)
            issues.Add($"ghost feature extractor version must be {GhostSourceExtractionPolicy.CurrentBackendVersion}");
        if (string.IsNullOrWhiteSpace(ExtractionPolicyId)) issues.Add("ghost extraction policy ID is missing");
        if (!IsSha256(ExtractionPolicySha256)) issues.Add("ghost extraction policy SHA-256 is invalid");
        if (!IsSha256(OpticalTopologySha256)) issues.Add("ghost optical-topology SHA-256 is invalid");
        issues.AddRange(Detector.Validate());
        if (!IsSha256(OrientationFingerprintSha256)) issues.Add("ghost orientation fingerprint SHA-256 is invalid");
        if (!double.IsFinite(OrientationDegrees)) issues.Add("ghost orientation angle is not finite");
        if (string.IsNullOrWhiteSpace(PierSide)) issues.Add("ghost calibration pier side is missing");
        if (Gain < 0) issues.Add("ghost calibration gain is invalid");
        if (MinimumExposureMilliseconds <= 0 || MaximumExposureMilliseconds < MinimumExposureMilliseconds)
            issues.Add("ghost calibration exposure range is invalid");
        if (CreatedUtc == default || ValidUntilUtc <= CreatedUtc)
            issues.Add("ghost calibration validity interval is invalid");
        if (!Positive(CalibrationRmsResidualPixels) || !Positive(CalibrationMaximumResidualPixels) ||
            CalibrationMaximumResidualPixels < CalibrationRmsResidualPixels)
            issues.Add("ghost calibration residual bounds are invalid");
        if (!TargetSystematicCovariancePixelsSquared.IsPositiveSemidefinite)
            issues.Add("ghost target systematic covariance is invalid");
        if (Features is null || Features.Count == 0)
        {
            issues.Add("ghost calibration contains no template features");
        }
        else
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var feature in Features)
            {
                if (string.IsNullOrWhiteSpace(feature.FeatureId) || !ids.Add(feature.FeatureId))
                    issues.Add("ghost template feature IDs must be non-empty and unique");
                if (!Finite(feature.OffsetFromTarget)) issues.Add($"ghost feature {feature.FeatureId} offset is not finite");
                if (!Positive(feature.RelativeFlux)) issues.Add($"ghost feature {feature.FeatureId} relative flux is invalid");
                if (!feature.OffsetCovariancePixelsSquared.IsPositiveSemidefinite)
                    issues.Add($"ghost feature {feature.FeatureId} covariance is invalid");
            }
        }
        if (!IsSha256(CalibrationEvidenceSha256)) issues.Add("ghost calibration evidence SHA-256 is invalid");
        if (!HasValidContentSha256()) issues.Add("ghost calibration content SHA-256 does not match its payload");
        return issues.AsReadOnly();
    }

    public static string ComputeContentSha256(GhostTemplateCalibration calibration)
    {
        ArgumentNullException.ThrowIfNull(calibration);
        var payload = new HashPayload(
            calibration.SchemaVersion,
            calibration.CalibrationId,
            calibration.InstallationEpochId,
            calibration.CameraStableId,
            calibration.Phd2ProfileId,
            calibration.ExtractorKind,
            calibration.ExtractorVersion,
            calibration.ExtractionPolicyId,
            calibration.ExtractionPolicySha256,
            calibration.OpticalTopologySha256,
            calibration.Detector,
            calibration.OrientationFingerprintSha256,
            calibration.OrientationDegrees,
            calibration.PierSide,
            calibration.Gain,
            calibration.MinimumExposureMilliseconds,
            calibration.MaximumExposureMilliseconds,
            calibration.CreatedUtc,
            calibration.ValidUntilUtc,
            calibration.CalibrationRmsResidualPixels,
            calibration.CalibrationMaximumResidualPixels,
            calibration.TargetSystematicCovariancePixelsSquared,
            calibration.Features,
            calibration.CalibrationEvidenceSha256);
        var json = JsonSerializer.Serialize(payload);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    private sealed record HashPayload(
        int SchemaVersion,
        string CalibrationId,
        string InstallationEpochId,
        string CameraStableId,
        string Phd2ProfileId,
        GhostFeatureExtractorKind ExtractorKind,
        int ExtractorVersion,
        string ExtractionPolicyId,
        string ExtractionPolicySha256,
        string OpticalTopologySha256,
        GhostDetectorGeometry Detector,
        string OrientationFingerprintSha256,
        double OrientationDegrees,
        string PierSide,
        int Gain,
        int MinimumExposureMilliseconds,
        int MaximumExposureMilliseconds,
        DateTimeOffset CreatedUtc,
        DateTimeOffset ValidUntilUtc,
        double CalibrationRmsResidualPixels,
        double CalibrationMaximumResidualPixels,
        GhostCovariance2D TargetSystematicCovariancePixelsSquared,
        IReadOnlyList<GhostTemplateFeature> Features,
        string CalibrationEvidenceSha256);

    internal static bool IsSha256(string? value)
    {
        var normalized = (value ?? string.Empty).Replace("-", string.Empty, StringComparison.Ordinal).Trim();
        return normalized.Length == 64 && normalized.All(Uri.IsHexDigit);
    }

    private static bool Positive(double value) => double.IsFinite(value) && value > 0;
    private static bool Finite(PixelPoint point) => double.IsFinite(point.X) && double.IsFinite(point.Y);
}

/// <summary>
/// Numeric gates are a versioned commissioning policy. This type deliberately
/// has no equipment-specific defaults.
/// </summary>
public sealed record GhostTemplatePolicy(
    int SchemaVersion,
    string PolicyId,
    TimeSpan MaximumCalibrationAge,
    TimeSpan MaximumFrameAge,
    TimeSpan MaximumFrameSpan,
    double MaximumOrientationDifferenceDegrees,
    int MinimumFrameCount,
    int MinimumMatchedFeatures,
    double MaximumFeatureResidualPixels,
    double MaximumRelativeFluxLogResidual,
    double MaximumExposureNormalizedFluxLogScatter,
    double MaximumCommonMotionResidualPixels,
    double MaximumRegisteredTargetScatterPixels,
    double MinimumUniquenessLikelihoodRatio,
    double CandidateMergeRadiusPixels,
    double EdgeMarginPixels,
    double MaximumTargetUncertaintyPixels)
{
    public const int CurrentSchemaVersion = 1;

    public IReadOnlyList<string> Validate()
    {
        var issues = new List<string>();
        if (SchemaVersion != CurrentSchemaVersion) issues.Add($"ghost policy schema must be {CurrentSchemaVersion}");
        if (string.IsNullOrWhiteSpace(PolicyId)) issues.Add("ghost policy ID is missing");
        if (MaximumCalibrationAge <= TimeSpan.Zero) issues.Add("ghost maximum calibration age must be positive");
        if (MaximumFrameAge <= TimeSpan.Zero) issues.Add("ghost maximum frame age must be positive");
        if (MaximumFrameSpan <= TimeSpan.Zero) issues.Add("ghost maximum frame span must be positive");
        if (!NonNegative(MaximumOrientationDifferenceDegrees) || MaximumOrientationDifferenceDegrees > 180)
            issues.Add("ghost orientation tolerance must be finite and in [0, 180]");
        if (MinimumFrameCount < 2) issues.Add("ghost assistance requires at least two frames");
        if (MinimumMatchedFeatures < 1) issues.Add("ghost assistance requires at least one matched feature");
        if (!Positive(MaximumFeatureResidualPixels)) issues.Add("ghost feature residual limit must be positive");
        if (!Positive(MaximumRelativeFluxLogResidual)) issues.Add("ghost relative-flux residual limit must be positive");
        if (!Positive(MaximumExposureNormalizedFluxLogScatter)) issues.Add("ghost exposure-normalized flux scatter limit must be positive");
        if (!Positive(MaximumCommonMotionResidualPixels)) issues.Add("ghost common-motion residual limit must be positive");
        if (!Positive(MaximumRegisteredTargetScatterPixels)) issues.Add("ghost registered-target scatter limit must be positive");
        if (!double.IsFinite(MinimumUniquenessLikelihoodRatio) || MinimumUniquenessLikelihoodRatio <= 1)
            issues.Add("ghost uniqueness likelihood ratio must exceed one");
        if (!Positive(CandidateMergeRadiusPixels)) issues.Add("ghost candidate merge radius must be positive");
        if (!NonNegative(EdgeMarginPixels)) issues.Add("ghost edge margin must be finite and non-negative");
        if (!Positive(MaximumTargetUncertaintyPixels)) issues.Add("ghost target uncertainty limit must be positive");
        return issues.AsReadOnly();
    }

    private static bool Positive(double value) => double.IsFinite(value) && value > 0;
    private static bool NonNegative(double value) => double.IsFinite(value) && value >= 0;
}

public sealed record GhostExternalIdentityEvidence(
    string ObservationRunId,
    string CatalogId,
    GhostExternalIdentityAuthority Authority,
    GateResult Gate,
    string EvidenceSha256,
    DateTimeOffset VerifiedUtc,
    DateTimeOffset ValidUntilUtc);

public sealed record GhostRuntimeBinding(
    string ObservationRunId,
    string CatalogId,
    DateTimeOffset EvaluatedUtc,
    string InstallationEpochId,
    string CameraStableId,
    string Phd2ProfileId,
    GhostFeatureExtractorKind ExtractorKind,
    int ExtractorVersion,
    string ExtractionPolicyId,
    string ExtractionPolicySha256,
    string OpticalTopologySha256,
    GhostDetectorGeometry Detector,
    string OrientationFingerprintSha256,
    double OrientationDegrees,
    string PierSide,
    GhostExternalIdentityEvidence ExternalIdentity);

public sealed record GhostSourceDetection(
    string DetectionId,
    PixelPoint Centroid,
    double IntegratedFluxAdu,
    double CentroidSigmaPixels,
    double EdgeDistancePixels,
    bool Saturated = false,
    bool Blended = false);

/// <summary>
/// ExpectedDetectorMotionFromFirstFrame is optional evidence from a separately
/// bounded, already-accounted operation. This algorithm never commands that
/// motion. When absent, common motion is tested against the fitted target
/// translation and the registered target must remain stable.
/// </summary>
public sealed record GhostFrameObservation(
    string FrameId,
    string FrameSha256,
    DateTimeOffset CompletedUtc,
    int Width,
    int Height,
    int ExposureMilliseconds,
    int Gain,
    IReadOnlyList<GhostSourceDetection> Detections,
    GhostFeatureExtractorKind ExtractorKind,
    int ExtractorVersion,
    string ExtractionPolicyId,
    string ExtractionPolicySha256,
    string SourceExtractionEvidenceSha256,
    PixelPoint? ExpectedDetectorMotionFromFirstFrame = null,
    string? ExpectedMotionEvidenceSha256 = null);

public sealed record GhostFeatureMatch(
    string FeatureId,
    string DetectionId,
    PixelPoint DetectionCentroid,
    PixelPoint TargetEstimate,
    double SpatialResidualPixels,
    double RelativeFluxLogResidual);

public sealed record GhostFrameMatch(
    string FrameId,
    PixelPoint TargetCentroid,
    GhostCovariance2D TargetCovariancePixelsSquared,
    double TargetUncertaintyPixels,
    double SpatialRmsPixels,
    double RelativeFluxLogRms,
    double ExposureNormalizedFluxScale,
    double UniquenessLikelihoodRatio,
    IReadOnlyList<GhostFeatureMatch> Features);

public sealed record GhostAssistanceResult(
    GhostAssistanceDecision Decision,
    GateResult Gate,
    GateResult TemplateGate,
    GhostLocatorAuthority Authority,
    PixelPoint? EstimatedTargetCentroid,
    GhostCovariance2D? EstimatedTargetCovariancePixelsSquared,
    double TargetUncertaintyPixels,
    double UniquenessLikelihoodRatio,
    IReadOnlyList<GhostFrameMatch> FrameMatches)
{
    /// <summary>Always false by design; catalogue identity comes from ExternalIdentity.</summary>
    public bool CanEstablishTargetIdentity => false;
}

public static class GhostTemplateAssistance
{
    public static GhostAssistanceResult Evaluate(
        GhostAssistanceMode mode,
        GhostTemplateCalibration? calibration,
        GhostTemplatePolicy policy,
        GhostRuntimeBinding binding,
        IReadOnlyList<GhostFrameObservation> frames)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(binding);
        frames ??= Array.Empty<GhostFrameObservation>();

        if (mode == GhostAssistanceMode.Skip)
        {
            var skipped = GateResult.Unknown(
                "GHOST_TEMPLATE_SKIPPED",
                "Ghost assistance was explicitly skipped; continue with fresh G3 long-exposure WCS and bounded small-move recovery.");
            return Fallback(mode, skipped);
        }

        var policyIssues = policy.Validate();
        if (policyIssues.Count > 0)
        {
            return Unavailable(mode, GateResult.Unknown(
                "GHOST_POLICY_INVALID",
                $"The versioned ghost policy is invalid: {string.Join("; ", policyIssues)}."));
        }

        if (calibration is null)
        {
            return Unavailable(mode, GateResult.Unknown(
                "GHOST_CALIBRATION_MISSING",
                "No versioned ghost-template calibration is available."));
        }

        var calibrationIssues = calibration.Validate();
        if (calibrationIssues.Count > 0)
        {
            return Unavailable(mode, GateResult.Unknown(
                "GHOST_CALIBRATION_INVALID",
                $"The ghost-template calibration is invalid: {string.Join("; ", calibrationIssues)}."));
        }

        var applicability = EvaluateApplicability(calibration, policy, binding, frames);
        if (applicability.Disposition != GateDisposition.Passed)
        {
            return Unavailable(mode, applicability);
        }

        var orderedFrames = frames.OrderBy(frame => frame.CompletedUtc).ToArray();
        var frameMatches = new List<GhostFrameMatch>(orderedFrames.Length);
        foreach (var frame in orderedFrames)
        {
            var match = MatchFrame(calibration, policy, frame);
            if (match.Gate.Disposition != GateDisposition.Passed || match.Match is null)
            {
                return Unavailable(mode, match.Gate, frameMatches);
            }
            frameMatches.Add(match.Match);
        }

        var temporal = EvaluateTemporalConsistency(calibration, policy, orderedFrames, frameMatches);
        if (temporal.Gate.Disposition != GateDisposition.Passed)
        {
            return Unavailable(mode, temporal.Gate, frameMatches);
        }

        var latest = frameMatches[^1];
        var uncertainty = latest.TargetUncertaintyPixels;
        var uniqueness = frameMatches.Min(match => match.UniquenessLikelihoodRatio);
        var metrics = new Dictionary<string, double>
        {
            ["frames"] = frameMatches.Count,
            ["matchedFeatures"] = frameMatches.Min(match => match.Features.Count),
            ["targetX"] = latest.TargetCentroid.X,
            ["targetY"] = latest.TargetCentroid.Y,
            ["targetUncertaintyPixels"] = uncertainty,
            ["minimumUniquenessLikelihoodRatio"] = FiniteOrLarge(uniqueness),
            ["commonMotionRmsPixels"] = temporal.CommonMotionRmsPixels,
            ["registeredTargetScatterPixels"] = temporal.RegisteredTargetScatterPixels,
            ["exposureNormalizedFluxLogScatter"] = temporal.FluxLogScatter,
            ["identityEstablishedByGhost"] = 0,
        };
        var gate = GateResult.Pass(
            "GHOST_AUXILIARY_TARGET_ESTIMATE_VALID",
            $"Calibration {calibration.CalibrationId} matched {frameMatches.Count} fresh G3 frames. " +
            $"The latest auxiliary target centroid is ({latest.TargetCentroid.X:F2}, {latest.TargetCentroid.Y:F2}) px " +
            $"with {uncertainty:F2} px one-sigma uncertainty. Catalogue identity remains external; this result is not mount authority.",
            metrics);
        return new GhostAssistanceResult(
            GhostAssistanceDecision.UseCalibratedAuxiliaryEstimate,
            gate,
            gate,
            GhostLocatorAuthority.CalibratedAuxiliaryOnly,
            latest.TargetCentroid,
            latest.TargetCovariancePixelsSquared,
            uncertainty,
            uniqueness,
            frameMatches.AsReadOnly());
    }

    private static GateResult EvaluateApplicability(
        GhostTemplateCalibration calibration,
        GhostTemplatePolicy policy,
        GhostRuntimeBinding binding,
        IReadOnlyList<GhostFrameObservation> frames)
    {
        var failures = new List<string>();
        if (string.IsNullOrWhiteSpace(binding.ObservationRunId) || string.IsNullOrWhiteSpace(binding.CatalogId))
            failures.Add("runtime observation run/catalog identity is incomplete");
        if (!string.Equals(calibration.InstallationEpochId, binding.InstallationEpochId, StringComparison.Ordinal))
            failures.Add("installation epoch changed");
        if (!string.Equals(calibration.CameraStableId, binding.CameraStableId, StringComparison.OrdinalIgnoreCase))
            failures.Add("G3 camera stable identity changed");
        if (!string.Equals(calibration.Phd2ProfileId, binding.Phd2ProfileId, StringComparison.Ordinal))
            failures.Add("PHD2 profile changed");
        if (calibration.ExtractorKind != binding.ExtractorKind || calibration.ExtractorVersion != binding.ExtractorVersion)
            failures.Add("ghost feature-extractor backend/version changed");
        if (!string.Equals(calibration.ExtractionPolicyId, binding.ExtractionPolicyId, StringComparison.Ordinal) ||
            !string.Equals(calibration.ExtractionPolicySha256, binding.ExtractionPolicySha256, StringComparison.OrdinalIgnoreCase))
            failures.Add("ghost source-extraction policy changed");
        if (!string.Equals(calibration.OpticalTopologySha256, binding.OpticalTopologySha256, StringComparison.OrdinalIgnoreCase))
            failures.Add("optical topology fingerprint changed");
        if (calibration.Detector != binding.Detector)
            failures.Add("G3 ROI or binning changed");
        if (!string.Equals(calibration.OrientationFingerprintSha256, binding.OrientationFingerprintSha256, StringComparison.OrdinalIgnoreCase))
            failures.Add("G3 orientation fingerprint changed");
        var orientationDifference = AngleDifferenceDegrees(calibration.OrientationDegrees, binding.OrientationDegrees);
        if (!double.IsFinite(orientationDifference) || orientationDifference > policy.MaximumOrientationDifferenceDegrees)
            failures.Add("G3 sensor orientation is outside the commissioned tolerance");
        if (!string.Equals(calibration.PierSide, binding.PierSide, StringComparison.OrdinalIgnoreCase))
            failures.Add("mount pier side changed");
        if (binding.EvaluatedUtc < calibration.CreatedUtc || binding.EvaluatedUtc > calibration.ValidUntilUtc ||
            binding.EvaluatedUtc - calibration.CreatedUtc > policy.MaximumCalibrationAge)
            failures.Add("ghost calibration is stale, future-dated, or outside its validity interval");

        var identity = binding.ExternalIdentity;
        if (identity is null || identity.Gate.Disposition != GateDisposition.Passed)
            failures.Add("fresh external catalogue/WCS target identity did not pass");
        else
        {
            if (!Enum.IsDefined(identity.Authority))
                failures.Add("external target-identity authority is invalid");
            if (!string.Equals(identity.ObservationRunId, binding.ObservationRunId, StringComparison.Ordinal))
                failures.Add("external target identity belongs to another observation run");
            if (!string.Equals(identity.CatalogId, binding.CatalogId, StringComparison.Ordinal))
                failures.Add("external target identity belongs to another catalogue target");
            if (!GhostTemplateCalibration.IsSha256(identity.EvidenceSha256))
                failures.Add("external target-identity evidence SHA-256 is invalid");
            if (identity.VerifiedUtc == default || identity.VerifiedUtc > binding.EvaluatedUtc.AddSeconds(5) ||
                identity.ValidUntilUtc <= identity.VerifiedUtc || binding.EvaluatedUtc > identity.ValidUntilUtc)
                failures.Add("external target identity is stale or future-dated");
        }

        if (frames.Count < policy.MinimumFrameCount)
            failures.Add($"only {frames.Count} ghost frames were supplied; {policy.MinimumFrameCount} are required");
        if (calibration.Features.Count < policy.MinimumMatchedFeatures)
            failures.Add("the calibration has fewer features than the policy requires");
        if (calibration.CalibrationMaximumResidualPixels > policy.MaximumFeatureResidualPixels)
            failures.Add("the calibration maximum residual exceeds the runtime feature-residual gate");

        var ordered = frames.OrderBy(frame => frame.CompletedUtc).ToArray();
        if (ordered.Length > 0)
        {
            var frameIds = new HashSet<string>(StringComparer.Ordinal);
            var frameHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (ordered[^1].CompletedUtc - ordered[0].CompletedUtc > policy.MaximumFrameSpan)
                failures.Add("ghost frame sequence exceeds its maximum span");
            for (var index = 0; index < ordered.Length; index++)
            {
                var frame = ordered[index];
                if (string.IsNullOrWhiteSpace(frame.FrameId) || !GhostTemplateCalibration.IsSha256(frame.FrameSha256))
                    failures.Add($"ghost frame {index + 1} identity/SHA-256 is invalid");
                else
                {
                    if (!frameIds.Add(frame.FrameId)) failures.Add($"ghost frame ID {frame.FrameId} is duplicated");
                    if (!frameHashes.Add(frame.FrameSha256)) failures.Add($"ghost frame SHA-256 {frame.FrameSha256} is duplicated");
                }
                if (frame.CompletedUtc == default || frame.CompletedUtc > binding.EvaluatedUtc.AddSeconds(5) ||
                    binding.EvaluatedUtc - frame.CompletedUtc > policy.MaximumFrameAge)
                    failures.Add($"ghost frame {frame.FrameId} is stale or future-dated");
                if (index > 0 && frame.CompletedUtc <= ordered[index - 1].CompletedUtc)
                    failures.Add("ghost frame timestamps must be strictly increasing");
                if (frame.Width != calibration.Detector.OutputWidth || frame.Height != calibration.Detector.OutputHeight)
                    failures.Add($"ghost frame {frame.FrameId} dimensions do not match the calibrated ROI/binning");
                if (frame.ExposureMilliseconds < calibration.MinimumExposureMilliseconds ||
                    frame.ExposureMilliseconds > calibration.MaximumExposureMilliseconds)
                    failures.Add($"ghost frame {frame.FrameId} exposure is outside the calibrated range");
                if (frame.Gain != calibration.Gain)
                    failures.Add($"ghost frame {frame.FrameId} gain differs from the calibrated gain");
                if (frame.ExtractorKind != binding.ExtractorKind || frame.ExtractorVersion != binding.ExtractorVersion)
                    failures.Add($"ghost frame {frame.FrameId} extractor backend/version differs from the runtime binding");
                if (!string.Equals(frame.ExtractionPolicyId, binding.ExtractionPolicyId, StringComparison.Ordinal) ||
                    !string.Equals(frame.ExtractionPolicySha256, binding.ExtractionPolicySha256, StringComparison.OrdinalIgnoreCase))
                    failures.Add($"ghost frame {frame.FrameId} extraction policy differs from the runtime binding");
                if (!GhostTemplateCalibration.IsSha256(frame.SourceExtractionEvidenceSha256))
                    failures.Add($"ghost frame {frame.FrameId} source-extraction evidence SHA-256 is invalid");
                if (frame.Detections is null || frame.Detections.Count == 0)
                    failures.Add($"ghost frame {frame.FrameId} has no deterministic source detections");
                if (frame.ExpectedDetectorMotionFromFirstFrame is not null &&
                    !Finite(frame.ExpectedDetectorMotionFromFirstFrame))
                    failures.Add($"ghost frame {frame.FrameId} expected motion is not finite");
                if (frame.ExpectedDetectorMotionFromFirstFrame is not null && index > 0 &&
                    !GhostTemplateCalibration.IsSha256(frame.ExpectedMotionEvidenceSha256))
                    failures.Add($"ghost frame {frame.FrameId} expected-motion evidence SHA-256 is invalid");
            }
        }

        var metrics = new Dictionary<string, double>
        {
            ["calibrationAgeHours"] = Math.Max(0, (binding.EvaluatedUtc - calibration.CreatedUtc).TotalHours),
            ["orientationDifferenceDegrees"] = double.IsFinite(orientationDifference) ? orientationDifference : 360,
            ["frames"] = frames.Count,
            ["identityGatePassed"] = identity?.Gate.Disposition == GateDisposition.Passed ? 1 : 0,
        };
        return failures.Count == 0
            ? GateResult.Pass(
                "GHOST_TEMPLATE_APPLICABLE",
                "The versioned ghost template matches the current installation, camera/profile topology, ROI/binning, orientation, pier side, exposure/gain, age, and fresh external identity.",
                metrics)
            : GateResult.Unknown(
                "GHOST_TEMPLATE_NOT_APPLICABLE",
                $"Ghost-template assistance is not applicable: {string.Join("; ", failures)}.",
                metrics);
    }

    private static FrameMatchResult MatchFrame(
        GhostTemplateCalibration calibration,
        GhostTemplatePolicy policy,
        GhostFrameObservation frame)
    {
        var detections = frame.Detections
            .Where(detection =>
                !string.IsNullOrWhiteSpace(detection.DetectionId) &&
                Finite(detection.Centroid) &&
                Positive(detection.IntegratedFluxAdu) &&
                Positive(detection.CentroidSigmaPixels) &&
                double.IsFinite(detection.EdgeDistancePixels) &&
                detection.EdgeDistancePixels >= policy.EdgeMarginPixels &&
                !detection.Saturated &&
                !detection.Blended)
            .ToArray();
        if (detections.Length < policy.MinimumMatchedFeatures)
        {
            return FrameMatchResult.Failure(
                "GHOST_DETECTIONS_INSUFFICIENT",
                $"Frame {frame.FrameId} has only {detections.Length} unsaturated, isolated, non-edge detections; {policy.MinimumMatchedFeatures} are required.");
        }

        var hypotheses = new List<FrameHypothesis>();
        foreach (var feature in calibration.Features)
        foreach (var detection in detections)
        {
            var seed = Subtract(detection.Centroid, feature.OffsetFromTarget);
            var hypothesis = FitHypothesis(calibration, policy, frame, detections, seed);
            if (hypothesis is not null) hypotheses.Add(hypothesis);
        }

        if (hypotheses.Count == 0)
        {
            return FrameMatchResult.Failure(
                "GHOST_PATTERN_NOT_FOUND",
                $"Frame {frame.FrameId} contains no source pattern satisfying the calibrated ghost geometry and relative-flux gates.");
        }

        var ranked = hypotheses
            .OrderByDescending(candidate => candidate.Matches.Count)
            .ThenBy(candidate => candidate.Cost)
            .ToArray();
        var best = ranked[0];
        var distinct = ranked
            .Skip(1)
            .Where(candidate => candidate.Matches.Count == best.Matches.Count &&
                                Distance(candidate.Target, best.Target) >= policy.CandidateMergeRadiusPixels)
            .FirstOrDefault();
        var uniqueness = distinct is null
            ? double.PositiveInfinity
            : Math.Exp(Math.Min(700, Math.Max(0, (distinct.Cost - best.Cost) / 2)));
        if (uniqueness < policy.MinimumUniquenessLikelihoodRatio)
        {
            return FrameMatchResult.Failure(
                "GHOST_PATTERN_AMBIGUOUS",
                $"Frame {frame.FrameId} has another distinct pattern hypothesis; likelihood ratio {uniqueness:F2} is below {policy.MinimumUniquenessLikelihoodRatio:F2}.",
                new Dictionary<string, double> { ["uniquenessLikelihoodRatio"] = uniqueness });
        }

        var targetEdge = Math.Min(
            Math.Min(best.Target.X, frame.Width - 1 - best.Target.X),
            Math.Min(best.Target.Y, frame.Height - 1 - best.Target.Y));
        if (targetEdge < policy.EdgeMarginPixels)
        {
            return FrameMatchResult.Failure(
                "GHOST_TARGET_NEAR_EDGE",
                $"Frame {frame.FrameId} predicts the target only {targetEdge:F2} px from an edge; {policy.EdgeMarginPixels:F2} px is required.");
        }
        if (best.UncertaintyPixels > policy.MaximumTargetUncertaintyPixels)
        {
            return FrameMatchResult.Failure(
                "GHOST_TARGET_UNCERTAINTY_HIGH",
                $"Frame {frame.FrameId} target uncertainty {best.UncertaintyPixels:F2} px exceeds {policy.MaximumTargetUncertaintyPixels:F2} px.");
        }

        var match = new GhostFrameMatch(
            frame.FrameId,
            best.Target,
            best.Covariance,
            best.UncertaintyPixels,
            best.SpatialRms,
            best.FluxLogRms,
            best.ExposureNormalizedFluxScale,
            uniqueness,
            best.Matches);
        return new FrameMatchResult(
            GateResult.Pass(
                "GHOST_FRAME_PATTERN_VALID",
                $"Frame {frame.FrameId} uniquely matched {best.Matches.Count} calibrated ghost features.",
                new Dictionary<string, double>
                {
                    ["matchedFeatures"] = best.Matches.Count,
                    ["spatialRmsPixels"] = best.SpatialRms,
                    ["relativeFluxLogRms"] = best.FluxLogRms,
                    ["targetUncertaintyPixels"] = best.UncertaintyPixels,
                    ["uniquenessLikelihoodRatio"] = FiniteOrLarge(uniqueness),
                }),
            match);
    }

    private static FrameHypothesis? FitHypothesis(
        GhostTemplateCalibration calibration,
        GhostTemplatePolicy policy,
        GhostFrameObservation frame,
        IReadOnlyList<GhostSourceDetection> detections,
        PixelPoint seed)
    {
        var target = seed;
        List<(GhostTemplateFeature Feature, GhostSourceDetection Detection)> assignments = new();
        for (var iteration = 0; iteration < 3; iteration++)
        {
            assignments = Assign(calibration.Features, detections, target, policy.MaximumFeatureResidualPixels);
            if (assignments.Count < policy.MinimumMatchedFeatures) return null;
            target = WeightedTarget(calibration, assignments).Target;
        }

        var weighted = WeightedTarget(calibration, assignments);
        target = weighted.Target;
        var spatialResiduals = assignments
            .Select(pair => Distance(Subtract(pair.Detection.Centroid, pair.Feature.OffsetFromTarget), target))
            .ToArray();
        if (spatialResiduals.Any(residual => residual > policy.MaximumFeatureResidualPixels)) return null;
        var spatialRms = Rms(spatialResiduals);

        var logScales = assignments
            .Select(pair => Math.Log(pair.Detection.IntegratedFluxAdu /
                                     (pair.Feature.RelativeFlux * frame.ExposureMilliseconds)))
            .ToArray();
        var meanLogScale = logScales.Average();
        var fluxResiduals = logScales.Select(value => value - meanLogScale).ToArray();
        var fluxLogRms = Rms(fluxResiduals);
        if (fluxLogRms > policy.MaximumRelativeFluxLogResidual) return null;

        var scatterVariance = spatialRms * spatialRms;
        // CalibrationRmsResidualPixels is a shared template-fit uncertainty,
        // not independent noise that can be averaged away by matching more
        // ghosts. Keep it as a systematic term in the final target covariance.
        var calibrationVariance = calibration.CalibrationRmsResidualPixels *
                                  calibration.CalibrationRmsResidualPixels;
        var covariance = new GhostCovariance2D(
            weighted.Covariance.XX + calibration.TargetSystematicCovariancePixelsSquared.XX + scatterVariance + calibrationVariance,
            weighted.Covariance.XY + calibration.TargetSystematicCovariancePixelsSquared.XY,
            weighted.Covariance.YY + calibration.TargetSystematicCovariancePixelsSquared.YY + scatterVariance + calibrationVariance);
        var uncertainty = covariance.MaximumSigmaPixels;
        var matches = assignments
            .Select((pair, index) => new GhostFeatureMatch(
                pair.Feature.FeatureId,
                pair.Detection.DetectionId,
                pair.Detection.Centroid,
                Subtract(pair.Detection.Centroid, pair.Feature.OffsetFromTarget),
                spatialResiduals[index],
                fluxResiduals[index]))
            .OrderBy(match => match.FeatureId, StringComparer.Ordinal)
            .ToArray();
        var cost =
            Math.Pow(spatialRms / policy.MaximumFeatureResidualPixels, 2) +
            Math.Pow(fluxLogRms / policy.MaximumRelativeFluxLogResidual, 2) +
            Math.Pow(uncertainty / policy.MaximumTargetUncertaintyPixels, 2);
        return new FrameHypothesis(
            target,
            covariance,
            uncertainty,
            spatialRms,
            fluxLogRms,
            Math.Exp(meanLogScale),
            cost,
            matches);
    }

    private static List<(GhostTemplateFeature Feature, GhostSourceDetection Detection)> Assign(
        IReadOnlyList<GhostTemplateFeature> features,
        IReadOnlyList<GhostSourceDetection> detections,
        PixelPoint target,
        double maximumResidual)
    {
        var possible = new List<(double Distance, GhostTemplateFeature Feature, GhostSourceDetection Detection)>();
        foreach (var feature in features)
        {
            var expected = Add(target, feature.OffsetFromTarget);
            foreach (var detection in detections)
            {
                var distance = Distance(expected, detection.Centroid);
                if (distance <= maximumResidual) possible.Add((distance, feature, detection));
            }
        }

        var usedFeatures = new HashSet<string>(StringComparer.Ordinal);
        var usedDetections = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<(GhostTemplateFeature Feature, GhostSourceDetection Detection)>();
        foreach (var candidate in possible.OrderBy(candidate => candidate.Distance))
        {
            if (!usedFeatures.Add(candidate.Feature.FeatureId)) continue;
            if (!usedDetections.Add(candidate.Detection.DetectionId))
            {
                usedFeatures.Remove(candidate.Feature.FeatureId);
                continue;
            }
            result.Add((candidate.Feature, candidate.Detection));
        }
        return result;
    }

    private static WeightedTargetResult WeightedTarget(
        GhostTemplateCalibration calibration,
        IReadOnlyList<(GhostTemplateFeature Feature, GhostSourceDetection Detection)> assignments)
    {
        // Information-form fusion for independent per-feature centroid estimates.
        var infoXX = 0d;
        var infoXY = 0d;
        var infoYY = 0d;
        var rhsX = 0d;
        var rhsY = 0d;
        foreach (var pair in assignments)
        {
            var measurement = Subtract(pair.Detection.Centroid, pair.Feature.OffsetFromTarget);
            // Per-detection centroid noise and per-feature covariance are
            // independent. The calibration RMS is applied once later as a
            // shared systematic and therefore is intentionally absent here.
            var noise = pair.Detection.CentroidSigmaPixels * pair.Detection.CentroidSigmaPixels;
            var xx = pair.Feature.OffsetCovariancePixelsSquared.XX + noise;
            var xy = pair.Feature.OffsetCovariancePixelsSquared.XY;
            var yy = pair.Feature.OffsetCovariancePixelsSquared.YY + noise;
            var determinant = xx * yy - xy * xy;
            if (!(determinant > 1e-15))
            {
                xx += 1e-6;
                yy += 1e-6;
                determinant = xx * yy - xy * xy;
            }
            var invXX = yy / determinant;
            var invXY = -xy / determinant;
            var invYY = xx / determinant;
            infoXX += invXX;
            infoXY += invXY;
            infoYY += invYY;
            rhsX += invXX * measurement.X + invXY * measurement.Y;
            rhsY += invXY * measurement.X + invYY * measurement.Y;
        }

        var infoDeterminant = infoXX * infoYY - infoXY * infoXY;
        if (!(infoDeterminant > 1e-15))
        {
            var fallback = assignments
                .Select(pair => Subtract(pair.Detection.Centroid, pair.Feature.OffsetFromTarget))
                .ToArray();
            return new WeightedTargetResult(
                new PixelPoint(fallback.Average(point => point.X), fallback.Average(point => point.Y)),
                new GhostCovariance2D(double.MaxValue, 0, double.MaxValue));
        }
        var covariance = new GhostCovariance2D(
            infoYY / infoDeterminant,
            -infoXY / infoDeterminant,
            infoXX / infoDeterminant);
        return new WeightedTargetResult(
            new PixelPoint(
                covariance.XX * rhsX + covariance.XY * rhsY,
                covariance.XY * rhsX + covariance.YY * rhsY),
            covariance);
    }

    private static TemporalResult EvaluateTemporalConsistency(
        GhostTemplateCalibration calibration,
        GhostTemplatePolicy policy,
        IReadOnlyList<GhostFrameObservation> frames,
        IReadOnlyList<GhostFrameMatch> matches)
    {
        var referenceFrame = frames[0];
        var referenceMatch = matches[0];
        var referenceFeatures = referenceMatch.Features.ToDictionary(feature => feature.FeatureId, StringComparer.Ordinal);
        var registeredTargets = new List<PixelPoint>(matches.Count);
        var commonResiduals = new List<double>();
        var logFluxScales = new List<double>(matches.Count);

        for (var frameIndex = 0; frameIndex < matches.Count; frameIndex++)
        {
            var frame = frames[frameIndex];
            var match = matches[frameIndex];
            var fittedShift = Subtract(match.TargetCentroid, referenceMatch.TargetCentroid);
            var expectedShift = frame.ExpectedDetectorMotionFromFirstFrame ?? fittedShift;
            registeredTargets.Add(Subtract(match.TargetCentroid, expectedShift));
            logFluxScales.Add(Math.Log(match.ExposureNormalizedFluxScale));

            foreach (var feature in match.Features)
            {
                if (!referenceFeatures.TryGetValue(feature.FeatureId, out var referenceFeature)) continue;
                var observedShift = Subtract(feature.DetectionCentroid, referenceFeature.DetectionCentroid);
                commonResiduals.Add(Distance(observedShift, expectedShift));
            }
        }

        var commonMotionRms = Rms(commonResiduals);
        var targetCenter = new PixelPoint(
            registeredTargets.Average(point => point.X),
            registeredTargets.Average(point => point.Y));
        var registeredTargetScatter = Rms(registeredTargets.Select(point => Distance(point, targetCenter)));
        var fluxMean = logFluxScales.Average();
        var fluxLogScatter = Rms(logFluxScales.Select(value => value - fluxMean));
        var failures = new List<string>();
        if (commonResiduals.Count < policy.MinimumMatchedFeatures * frames.Count)
            failures.Add("too few cross-frame feature tracks establish common motion");
        if (commonMotionRms > policy.MaximumCommonMotionResidualPixels)
            failures.Add($"common-motion RMS {commonMotionRms:F2} px exceeds {policy.MaximumCommonMotionResidualPixels:F2} px");
        if (registeredTargetScatter > policy.MaximumRegisteredTargetScatterPixels)
            failures.Add($"registered target scatter {registeredTargetScatter:F2} px exceeds {policy.MaximumRegisteredTargetScatterPixels:F2} px");
        if (fluxLogScatter > policy.MaximumExposureNormalizedFluxLogScatter)
            failures.Add($"exposure-normalized flux log scatter {fluxLogScatter:F3} exceeds {policy.MaximumExposureNormalizedFluxLogScatter:F3}");

        var metrics = new Dictionary<string, double>
        {
            ["commonMotionRmsPixels"] = commonMotionRms,
            ["registeredTargetScatterPixels"] = registeredTargetScatter,
            ["exposureNormalizedFluxLogScatter"] = fluxLogScatter,
            ["trackedFeatureSamples"] = commonResiduals.Count,
        };
        return failures.Count == 0
            ? new TemporalResult(
                GateResult.Pass(
                    "GHOST_MULTIFRAME_CONSISTENT",
                    "Ghost features share one detector translation across fresh frames and retain their exposure-normalized relative flux pattern.",
                    metrics),
                commonMotionRms,
                registeredTargetScatter,
                fluxLogScatter)
            : new TemporalResult(
                GateResult.Unknown(
                    "GHOST_MULTIFRAME_INCONSISTENT",
                    $"Ghost multi-frame consistency failed: {string.Join("; ", failures)}.",
                    metrics),
                commonMotionRms,
                registeredTargetScatter,
                fluxLogScatter);
    }

    private static GhostAssistanceResult Unavailable(
        GhostAssistanceMode mode,
        GateResult templateGate,
        IReadOnlyList<GhostFrameMatch>? frameMatches = null)
    {
        if (mode == GhostAssistanceMode.RequireValid)
        {
            return new GhostAssistanceResult(
                GhostAssistanceDecision.PauseNeedsAttention,
                GateResult.Unknown(
                    "GHOST_REQUIRED_ASSISTANCE_UNAVAILABLE",
                    $"Ghost assistance was explicitly required but did not pass. {templateGate.Message}"),
                templateGate,
                GhostLocatorAuthority.None,
                null,
                null,
                double.PositiveInfinity,
                0,
                frameMatches ?? Array.Empty<GhostFrameMatch>());
        }
        return Fallback(mode, templateGate, frameMatches);
    }

    private static GhostAssistanceResult Fallback(
        GhostAssistanceMode mode,
        GateResult templateGate,
        IReadOnlyList<GhostFrameMatch>? frameMatches = null) => new(
        GhostAssistanceDecision.ContinueLongExposureWcsFallback,
        GateResult.Pass(
            mode == GhostAssistanceMode.Skip
                ? "GHOST_ASSISTANCE_SKIPPED_FALLBACK"
                : "GHOST_ASSISTANCE_INVALID_FALLBACK",
            $"{templateGate.Message} Continue with fresh G3 long-exposure WCS, N.I.N.A. bounded WCS centering, and bounded small-move/re-solve recovery; no ghost-derived motion is authorized."),
        templateGate,
        GhostLocatorAuthority.None,
        null,
        null,
        double.PositiveInfinity,
        0,
        frameMatches ?? Array.Empty<GhostFrameMatch>());

    private static PixelPoint Add(PixelPoint left, PixelPoint right) => new(left.X + right.X, left.Y + right.Y);
    private static PixelPoint Subtract(PixelPoint left, PixelPoint right) => new(left.X - right.X, left.Y - right.Y);
    private static double Distance(PixelPoint left, PixelPoint right)
    {
        var dx = left.X - right.X;
        var dy = left.Y - right.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
    private static bool Finite(PixelPoint point) => double.IsFinite(point.X) && double.IsFinite(point.Y);
    private static bool Positive(double value) => double.IsFinite(value) && value > 0;
    private static double Rms(IEnumerable<double> values)
    {
        var materialized = values.ToArray();
        return materialized.Length == 0 ? double.PositiveInfinity : Math.Sqrt(materialized.Average(value => value * value));
    }
    private static double AngleDifferenceDegrees(double left, double right)
    {
        if (!double.IsFinite(left) || !double.IsFinite(right)) return double.PositiveInfinity;
        var delta = (left - right) % 360;
        if (delta > 180) delta -= 360;
        if (delta < -180) delta += 360;
        return Math.Abs(delta);
    }
    private static double FiniteOrLarge(double value) => double.IsFinite(value) ? value : double.MaxValue;

    private sealed record WeightedTargetResult(PixelPoint Target, GhostCovariance2D Covariance);
    private sealed record FrameHypothesis(
        PixelPoint Target,
        GhostCovariance2D Covariance,
        double UncertaintyPixels,
        double SpatialRms,
        double FluxLogRms,
        double ExposureNormalizedFluxScale,
        double Cost,
        IReadOnlyList<GhostFeatureMatch> Matches);
    private sealed record FrameMatchResult(GateResult Gate, GhostFrameMatch? Match)
    {
        public static FrameMatchResult Failure(
            string code,
            string message,
            IReadOnlyDictionary<string, double>? metrics = null) =>
            new(GateResult.Unknown(code, message, metrics), null);
    }
    private sealed record TemporalResult(
        GateResult Gate,
        double CommonMotionRmsPixels,
        double RegisteredTargetScatterPixels,
        double FluxLogScatter);
}
