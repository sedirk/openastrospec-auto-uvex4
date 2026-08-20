using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UvexAdv.Observatory;

namespace UvexAdv.Commissioning.Tool;

public sealed record GhostRuntimeFingerprintContract(
    string InstallationEpochId,
    string OpticalTopologySha256,
    string OrientationFingerprintSha256,
    double OrientationDegrees);

/// <summary>
/// Strongly typed mirror of the production plugin's ghost-assistance
/// commissioning envelope. The public deterministic Observatory calibration,
/// match-policy and extraction-policy records remain the single algorithmic
/// source of validation and content hashing.
/// </summary>
public sealed record GhostAssistanceContract(
    int SchemaVersion,
    string BindingId,
    GhostTemplateCalibration? Calibration,
    GhostTemplatePolicy? MatchPolicy,
    string MatchPolicySha256,
    GhostSourceExtractionPolicy? ExtractionPolicy,
    string ExtractionPolicySha256,
    GhostRuntimeFingerprintContract? RuntimeFingerprint,
    TimeSpan MaximumExternalIdentityAge,
    double MaximumCatalogCoordinateMismatchArcseconds,
    double MaximumQhyTargetResidualArcseconds,
    double MinimumC11FocusConfidence)
{
    public const int CurrentSchemaVersion = 1;

    public IReadOnlyList<string> Validate()
    {
        var issues = new List<string>();
        if (SchemaVersion != CurrentSchemaVersion)
            issues.Add($"Ghost-assistance commissioning schema must be {CurrentSchemaVersion}.");
        if (string.IsNullOrWhiteSpace(BindingId))
            issues.Add("Ghost-assistance commissioning binding ID is missing.");
        if (Calibration is null)
            issues.Add("Ghost-template calibration is missing.");
        else
            issues.AddRange(Calibration.Validate());
        if (MatchPolicy is null)
        {
            issues.Add("Ghost-template match policy is missing.");
        }
        else
        {
            issues.AddRange(MatchPolicy.Validate());
            var computed = ComputeContentSha256(MatchPolicy);
            if (!SameHash(computed, MatchPolicySha256))
                issues.Add("Ghost-template match-policy SHA-256 does not match its complete payload.");
        }
        if (ExtractionPolicy is null)
        {
            issues.Add("Ghost source-extraction policy is missing.");
        }
        else
        {
            issues.AddRange(ExtractionPolicy.Validate());
            var computed = ExtractionPolicy.ComputeContentSha256();
            if (!SameHash(computed, ExtractionPolicySha256))
                issues.Add("Ghost source-extraction policy SHA-256 does not match its complete payload.");
            if (Calibration is not null &&
                (!string.Equals(Calibration.ExtractionPolicyId, ExtractionPolicy.PolicyId, StringComparison.Ordinal) ||
                 !SameHash(Calibration.ExtractionPolicySha256, computed) ||
                 Calibration.ExtractorKind != ExtractionPolicy.ExtractorKind ||
                 Calibration.ExtractorVersion != ExtractionPolicy.ExtractorVersion))
            {
                issues.Add("Ghost calibration and source-extraction policy ID/hash/backend binding do not match.");
            }
        }
        if (RuntimeFingerprint is null)
        {
            issues.Add("Ghost runtime installation/orientation fingerprint is missing.");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(RuntimeFingerprint.InstallationEpochId))
                issues.Add("Ghost runtime installation epoch is missing.");
            if (!IsSha256(RuntimeFingerprint.OpticalTopologySha256))
                issues.Add("Ghost runtime optical-topology SHA-256 is invalid.");
            if (!IsSha256(RuntimeFingerprint.OrientationFingerprintSha256))
                issues.Add("Ghost runtime orientation SHA-256 is invalid.");
            if (!double.IsFinite(RuntimeFingerprint.OrientationDegrees))
                issues.Add("Ghost runtime orientation angle is not finite.");
        }
        if (MaximumExternalIdentityAge <= TimeSpan.Zero)
            issues.Add("Ghost external catalogue/WCS identity age must be positive.");
        if (!double.IsFinite(MaximumCatalogCoordinateMismatchArcseconds) ||
            MaximumCatalogCoordinateMismatchArcseconds <= 0)
            issues.Add("Ghost external catalogue-coordinate mismatch limit must be positive and finite.");
        if (!double.IsFinite(MaximumQhyTargetResidualArcseconds) || MaximumQhyTargetResidualArcseconds <= 0)
            issues.Add("Ghost external QHY target-residual limit must be positive and finite.");
        if (!double.IsFinite(MinimumC11FocusConfidence) || MinimumC11FocusConfidence is <= 0 or > 1)
            issues.Add("Ghost independent C11 focus-confidence limit must be in (0, 1].");
        return issues.AsReadOnly();
    }

    public static string ComputeContentSha256<T>(T value)
    {
        var json = JsonSerializer.Serialize(value);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    private static bool SameHash(string? left, string? right) => string.Equals(
        NormalizeHash(left),
        NormalizeHash(right),
        StringComparison.OrdinalIgnoreCase);

    private static bool IsSha256(string? value) => NormalizeHash(value) is { Length: 64 } normalized &&
        normalized.All(Uri.IsHexDigit);

    private static string NormalizeHash(string? value) =>
        (value ?? string.Empty).Replace("-", string.Empty, StringComparison.Ordinal).Trim();
}
