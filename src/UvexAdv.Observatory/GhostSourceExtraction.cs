using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UvexAdv.Observatory;

/// <summary>
/// Versioned adapter policy for the existing deterministic G3 star-field
/// detector. All detection and morphology thresholds are explicit run
/// configuration; this adapter contains no camera acquisition or mount API.
/// </summary>
public sealed record GhostSourceExtractionPolicy(
    int SchemaVersion,
    string PolicyId,
    GhostFeatureExtractorKind ExtractorKind,
    int ExtractorVersion,
    StarDetectionOptions StarDetection,
    double MinimumSignalToNoise,
    double MaximumGhostEllipticity,
    double MinimumCentroidSigmaPixels,
    double MaximumCentroidSigmaPixels)
{
    public const int CurrentSchemaVersion = 1;
    public const int CurrentBackendVersion = 1;

    public string ComputeContentSha256()
    {
        var json = JsonSerializer.Serialize(this);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    public IReadOnlyList<string> Validate()
    {
        var issues = new List<string>();
        if (SchemaVersion != CurrentSchemaVersion) issues.Add($"ghost source-extraction schema must be {CurrentSchemaVersion}");
        if (string.IsNullOrWhiteSpace(PolicyId)) issues.Add("ghost source-extraction policy ID is missing");
        if (ExtractorKind != GhostFeatureExtractorKind.PointSourceStarFieldV1)
            issues.Add("ghost source-extraction backend is unsupported");
        if (ExtractorVersion != CurrentBackendVersion)
            issues.Add($"ghost source-extraction backend version must be {CurrentBackendVersion}");
        if (StarDetection is null) issues.Add("ghost source-extraction star-detector policy is missing");
        else
        {
            if (!Positive(StarDetection.DetectionSigma)) issues.Add("ghost detection sigma must be positive");
            if (StarDetection.CentroidRadiusPixels < 1) issues.Add("ghost centroid radius must be positive");
            if (StarDetection.EdgeMarginPixels < StarDetection.CentroidRadiusPixels)
                issues.Add("ghost star-detector edge margin must cover the centroid radius");
            if (StarDetection.MaximumCandidates < 1) issues.Add("ghost maximum source count must be positive");
            if (!FractionOrOne(StarDetection.MaximumEllipticity)) issues.Add("ghost star-detector ellipticity gate must be in [0, 1]");
            if (!FractionOrOne(StarDetection.MaximumSaturatedFraction)) issues.Add("ghost saturation fraction gate must be in [0, 1]");
        }
        if (!Positive(MinimumSignalToNoise)) issues.Add("ghost minimum SNR must be positive");
        if (!FractionOrOne(MaximumGhostEllipticity)) issues.Add("ghost morphology ellipticity gate must be in [0, 1]");
        if (!Positive(MinimumCentroidSigmaPixels) || !Positive(MaximumCentroidSigmaPixels) ||
            MaximumCentroidSigmaPixels < MinimumCentroidSigmaPixels)
            issues.Add("ghost centroid-uncertainty bounds are invalid");
        return issues.AsReadOnly();
    }

    private static bool Positive(double value) => double.IsFinite(value) && value > 0;
    private static bool FractionOrOne(double value) => double.IsFinite(value) && value >= 0 && value <= 1;
}

public sealed record GhostFrameCaptureMetadata(
    string FrameId,
    string FrameSha256,
    DateTimeOffset CompletedUtc,
    int ExposureMilliseconds,
    int Gain,
    PixelPoint? ExpectedDetectorMotionFromFirstFrame = null,
    string? ExpectedMotionEvidenceSha256 = null);

public sealed record GhostSourceOverlay(
    string DetectionId,
    PixelPoint Centroid,
    double SignalToNoise,
    double FwhmPixels,
    double Ellipticity,
    double SaturatedFraction,
    string Label);

/// <summary>
/// EvidenceSha256 binds the already-hashed immutable source frame, versioned
/// extraction policy, metadata, and deterministic detections. It can be used
/// as the overlay/evidence ID without modifying the raw G3 frame.
/// </summary>
public sealed record GhostSourceExtractionResult(
    GateResult Gate,
    GhostFrameObservation? Observation,
    string EvidenceSha256,
    IReadOnlyList<GhostSourceOverlay> OverlaySources);

public static class GhostFrameObservationFactory
{
    /// <summary>
    /// Runs only the in-memory deterministic StarFieldDetector. The caller
    /// remains responsible for obtaining and hashing the immutable G3 frame
    /// through PHD2, its sole owner.
    /// </summary>
    public static GhostSourceExtractionResult FromMonochromeFrame(
        MonochromeFrame frame,
        GhostFrameCaptureMetadata metadata,
        GhostSourceExtractionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(policy);
        var policyIssues = policy.Validate();
        if (policyIssues.Count > 0)
        {
            return Failure(
                "GHOST_SOURCE_POLICY_INVALID",
                $"Ghost source extraction policy is invalid: {string.Join("; ", policyIssues)}.");
        }
        var metadataIssues = ValidateMetadata(metadata);
        if (metadataIssues.Count > 0)
        {
            return Failure(
                "GHOST_SOURCE_FRAME_METADATA_INVALID",
                $"Ghost source frame metadata is invalid: {string.Join("; ", metadataIssues)}.");
        }

        var stars = StarFieldDetector.Detect(frame, policy.StarDetection);
        return FromStarCandidates(frame.Width, frame.Height, stars, metadata, policy);
    }

    /// <summary>
    /// Adapter for a caller that already ran the same deterministic G3 source
    /// detector. It performs no I/O, acquisition, camera connection, or motion.
    /// </summary>
    public static GhostSourceExtractionResult FromStarCandidates(
        int width,
        int height,
        IReadOnlyList<StarCandidate> candidates,
        GhostFrameCaptureMetadata metadata,
        GhostSourceExtractionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(policy);
        var policyIssues = policy.Validate();
        if (policyIssues.Count > 0)
        {
            return Failure(
                "GHOST_SOURCE_POLICY_INVALID",
                $"Ghost source extraction policy is invalid: {string.Join("; ", policyIssues)}.");
        }
        var metadataIssues = ValidateMetadata(metadata);
        if (metadataIssues.Count > 0 || width <= 0 || height <= 0)
        {
            if (width <= 0 || height <= 0) metadataIssues.Add("delivered frame dimensions must be positive");
            return Failure(
                "GHOST_SOURCE_FRAME_METADATA_INVALID",
                $"Ghost source frame metadata is invalid: {string.Join("; ", metadataIssues)}.");
        }

        var ordered = candidates
            .Where(candidate =>
                Finite(candidate.Centroid) &&
                Positive(candidate.FluxAdu) &&
                double.IsFinite(candidate.SignalToNoise) &&
                candidate.SignalToNoise >= policy.MinimumSignalToNoise &&
                Positive(candidate.FwhmPixels) &&
                double.IsFinite(candidate.Ellipticity) &&
                candidate.Ellipticity >= 0 &&
                double.IsFinite(candidate.SaturatedFraction) &&
                candidate.SaturatedFraction >= 0)
            .OrderBy(candidate => candidate.Centroid.Y)
            .ThenBy(candidate => candidate.Centroid.X)
            .ThenByDescending(candidate => candidate.FluxAdu)
            .ToArray();
        var detections = new List<GhostSourceDetection>(ordered.Length);
        var overlays = new List<GhostSourceOverlay>(ordered.Length);
        for (var index = 0; index < ordered.Length; index++)
        {
            var candidate = ordered[index];
            var id = $"{metadata.FrameId}:ghost-source:{index + 1:D4}";
            // For a near-Gaussian point source, sigma_centroid is approximately
            // sigma_PSF/SNR. Clamp only to explicit policy bounds so optimistic
            // SNR cannot manufacture zero uncertainty.
            var centroidSigma = Math.Clamp(
                candidate.FwhmPixels / (2.354820045 * candidate.SignalToNoise),
                policy.MinimumCentroidSigmaPixels,
                policy.MaximumCentroidSigmaPixels);
            var saturated = candidate.SaturatedFraction > 0;
            var blended = candidate.Ellipticity > policy.MaximumGhostEllipticity;
            detections.Add(new GhostSourceDetection(
                id,
                candidate.Centroid,
                candidate.FluxAdu,
                centroidSigma,
                candidate.EdgeDistancePixels,
                saturated,
                blended));
            overlays.Add(new GhostSourceOverlay(
                id,
                candidate.Centroid,
                candidate.SignalToNoise,
                candidate.FwhmPixels,
                candidate.Ellipticity,
                candidate.SaturatedFraction,
                saturated ? "saturated-rejected" : blended ? "elongated/blended-rejected" : "ghost-candidate"));
        }

        var extractionPolicySha256 = policy.ComputeContentSha256();
        var observation = new GhostFrameObservation(
            metadata.FrameId,
            metadata.FrameSha256,
            metadata.CompletedUtc,
            width,
            height,
            metadata.ExposureMilliseconds,
            metadata.Gain,
            detections.AsReadOnly(),
            policy.ExtractorKind,
            policy.ExtractorVersion,
            policy.PolicyId,
            extractionPolicySha256,
            string.Empty,
            metadata.ExpectedDetectorMotionFromFirstFrame,
            metadata.ExpectedMotionEvidenceSha256);
        var evidenceSha256 = ComputeEvidenceSha256(policy, observation, overlays);
        observation = observation with { SourceExtractionEvidenceSha256 = evidenceSha256 };
        var metrics = new Dictionary<string, double>
        {
            ["detectorCandidates"] = candidates.Count,
            ["retainedDetections"] = detections.Count,
            ["unsaturatedIsolatedDetections"] = detections.Count(detection => !detection.Saturated && !detection.Blended),
        };
        var gate = detections.Count > 0
            ? GateResult.Pass(
                "GHOST_SOURCE_EXTRACTION_COMPLETE",
                $"Deterministic G3 source extraction retained {detections.Count} detections; evidence/overlay SHA-256 is {evidenceSha256}.",
                metrics)
            : GateResult.Unknown(
                "GHOST_SOURCE_EXTRACTION_EMPTY",
                "Deterministic G3 source extraction found no source meeting the versioned SNR/morphology gates.",
                metrics);
        return new GhostSourceExtractionResult(gate, observation, evidenceSha256, overlays.AsReadOnly());
    }

    private static List<string> ValidateMetadata(GhostFrameCaptureMetadata metadata)
    {
        var issues = new List<string>();
        if (string.IsNullOrWhiteSpace(metadata.FrameId)) issues.Add("frame ID is missing");
        if (!GhostTemplateCalibration.IsSha256(metadata.FrameSha256)) issues.Add("frame SHA-256 is invalid");
        if (metadata.CompletedUtc == default) issues.Add("frame completion UTC is missing");
        if (metadata.ExposureMilliseconds <= 0) issues.Add("frame exposure must be positive");
        if (metadata.Gain < 0) issues.Add("frame gain is invalid");
        if (metadata.ExpectedDetectorMotionFromFirstFrame is not null && !Finite(metadata.ExpectedDetectorMotionFromFirstFrame))
            issues.Add("expected detector motion is not finite");
        if (metadata.ExpectedDetectorMotionFromFirstFrame is not null &&
            metadata.ExpectedDetectorMotionFromFirstFrame is { X: not 0 } or { Y: not 0 } &&
            !GhostTemplateCalibration.IsSha256(metadata.ExpectedMotionEvidenceSha256))
            issues.Add("non-zero expected detector motion lacks a valid evidence SHA-256");
        return issues;
    }

    private static string ComputeEvidenceSha256(
        GhostSourceExtractionPolicy policy,
        GhostFrameObservation observation,
        IReadOnlyList<GhostSourceOverlay> overlays)
    {
        var json = JsonSerializer.Serialize(new { policy, observation, overlays });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    private static GhostSourceExtractionResult Failure(string code, string message) =>
        new(GateResult.Unknown(code, message), null, string.Empty, Array.Empty<GhostSourceOverlay>());

    private static bool Positive(double value) => double.IsFinite(value) && value > 0;
    private static bool Finite(PixelPoint point) => double.IsFinite(point.X) && double.IsFinite(point.Y);
}
