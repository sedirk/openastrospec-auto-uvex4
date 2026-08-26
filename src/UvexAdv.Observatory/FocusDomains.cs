namespace UvexAdv.Observatory;

/// <summary>
/// The three optical paths whose focus mechanisms and evidence must never be
/// substituted for one another.
/// </summary>
public enum FocusDomainRole
{
    C11Main,
    Gs350WideField,
    UvexSpectral
}

public enum FocusMechanism
{
    Gemini,
    ToupTekAaf,
    UvexM2
}

public enum FocusApproachDirection
{
    None,
    IncreasingSteps,
    DecreasingSteps
}

public enum FocusMetricKind
{
    // G3 sees the C11 focal plane before the UVEX slit. UVEX M2 is downstream
    // and therefore cannot improve or satisfy this metric.
    G3StellarShape,
    QhyStellarShapeAndPlateSolve,
    AtrSpectralLineWidth
}

/// <summary>
/// A stable physical binding. HardwareInstanceId identifies one device
/// instance, while TopologyPath locks a physical USB route when the device can
/// otherwise reappear under the same family identifier.
/// </summary>
public sealed record FocusPhysicalBinding(
    FocusMechanism Mechanism,
    string ConnectionEndpoint,
    string HardwareInstanceId,
    string? TopologyPath);

public sealed record FocusMotionLimits(
    int MinimumPositionSteps,
    int MaximumPositionSteps,
    int MaximumSingleMoveSteps,
    int MaximumCumulativeMoveSteps,
    FocusApproachDirection ApproachDirection,
    int BacklashCompensationSteps);

public sealed record FocusMetricEvidence(
    FocusMetricKind Kind,
    string SourceCameraStableDeviceId,
    double Value,
    string Unit,
    string EvidenceSha256);

public sealed record FocusDomainBinding(
    FocusDomainRole Role,
    string Owner,
    string LogicalDeviceId,
    FocusPhysicalBinding PhysicalBinding,
    int StartPositionSteps,
    FocusMotionLimits Limits,
    FocusMetricEvidence Metric,
    DateTimeOffset VerifiedUtc,
    DateTimeOffset? ValidUntilUtc,
    double Confidence);

/// <summary>
/// A read-only live snapshot supplied by the actual owner of one focus domain.
/// It intentionally carries no movement API.
/// </summary>
public sealed record LiveFocusDomainState(
    FocusDomainRole Role,
    string Owner,
    string LogicalDeviceId,
    FocusPhysicalBinding PhysicalBinding,
    int? PositionSteps,
    LiveFocusMetricState? Metric = null);

/// <summary>
/// A metric evaluated from a current frame by the camera owner. ValidUntilUtc
/// is supplied with the evidence so compatibility never guesses how long a
/// frame remains representative.
/// </summary>
public sealed record LiveFocusMetricState(
    FocusMetricEvidence Evidence,
    DateTimeOffset VerifiedUtc,
    DateTimeOffset ValidUntilUtc,
    GateDisposition Disposition);

public static class FocusDomainConventions
{
    public const string C11Owner = "N.I.N.A.";
    public const string C11LogicalDeviceId = "ASCOM.StarFocuserPro.Focuser";
    public const string C11ConnectionEndpoint = "COM8";

    public const string Gs350LogicalDeviceId = "ASCOM.ToupTek.AAF";
    public const string Gs350ConnectionEndpoint = "AUTOFOCUSER";
    // The current release deliberately does not open or move the GS350 AAF.
    // Its truthful runtime owner is therefore the versioned manual-lock
    // contract below; the current QHY frame must independently attest focus.
    public const string Gs350Owner = "ManualOperator";

    public const string UvexOwner = "UvexAdv.Service";
    public const string UvexLogicalDeviceId = "UVEX4.M2";
    public const string UvexConnectionEndpoint = "COM5";

    private const string Ch340VidPid = "VID_1A86&PID_7523";
    private const string ToupTekVidPid = "VID_0547&PID_14AD";

    public static IReadOnlyList<string> ValidateBindings(NightSetupRecord setup)
    {
        var issues = new List<string>();
        var bindings = setup.FocusDomains;
        if (bindings is null)
        {
            issues.Add("Night Setup schema 2 requires explicit bindings for all three focus domains.");
            return issues;
        }

        if (bindings.Count != 3)
        {
            issues.Add($"Night Setup schema 2 requires exactly three focus-domain bindings; found {bindings.Count}.");
        }

        foreach (var duplicate in bindings
                     .GroupBy(binding => binding.Role)
                     .Where(group => group.Count() > 1)
                     .Select(group => group.Key))
        {
            issues.Add($"Focus domain role {duplicate} is bound more than once.");
        }

        foreach (var role in Enum.GetValues<FocusDomainRole>())
        {
            var roleBindings = bindings.Where(candidate => candidate.Role == role).ToArray();
            if (roleBindings.Length == 0)
            {
                issues.Add($"Focus domain role {role} is missing.");
                continue;
            }
            if (roleBindings.Length > 1) continue;
            ValidateBinding(setup, roleBindings[0], issues);
        }

        foreach (var duplicate in bindings
                     .Where(binding => !string.IsNullOrWhiteSpace(binding.LogicalDeviceId))
                     .GroupBy(binding => binding.LogicalDeviceId.Trim(), StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1))
        {
            issues.Add($"Logical focus identity '{duplicate.Key}' is assigned to more than one focus domain.");
        }

        return issues.AsReadOnly();
    }

    public static string Code(FocusDomainRole role) => role switch
    {
        FocusDomainRole.C11Main => "C11_MAIN",
        FocusDomainRole.Gs350WideField => "GS350_WIDE_FIELD",
        FocusDomainRole.UvexSpectral => "UVEX_SPECTRAL",
        _ => role.ToString().ToUpperInvariant(),
    };

    private static void ValidateBinding(NightSetupRecord setup, FocusDomainBinding binding, List<string> issues)
    {
        var label = $"Focus domain {binding.Role}";
        if (string.IsNullOrWhiteSpace(binding.Owner)) issues.Add($"{label} owner is required.");
        if (string.IsNullOrWhiteSpace(binding.LogicalDeviceId)) issues.Add($"{label} logical device identity is required.");
        if (binding.PhysicalBinding is null)
        {
            issues.Add($"{label} physical binding is required.");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(binding.PhysicalBinding.ConnectionEndpoint)) issues.Add($"{label} connection endpoint is required.");
            if (!HasSpecificHardwareInstance(binding.PhysicalBinding.HardwareInstanceId)) issues.Add($"{label} requires one exact hardware instance identity, not only a device family.");
        }

        ValidateLimits(binding, label, issues);
        ValidateMetric(setup, binding, label, issues);

        if (binding.VerifiedUtc == default) issues.Add($"{label} verification timestamp is required.");
        if (binding.VerifiedUtc > setup.LockedUtc.AddMinutes(5)) issues.Add($"{label} was verified after the Night Setup lock time.");
        // Installation/focus bindings are state-bound, not calendar-bound.
        // ValidUntilUtc remains readable for schema-2 backward compatibility,
        // but a supplied legacy value is only checked for internal ordering.
        // Runtime compatibility below re-reads the exact owner, physical
        // identity/topology, position and (where required) a fresh live metric.
        if (binding.ValidUntilUtc is { } validUntil && validUntil <= binding.VerifiedUtc)
            issues.Add($"{label} legacy validity deadline must be after its verification timestamp when supplied.");
        if (!double.IsFinite(binding.Confidence) || binding.Confidence <= 0 || binding.Confidence > 1)
        {
            issues.Add($"{label} confidence must be finite and in (0, 1].");
        }

        ValidateRoleSpecificBinding(binding, label, issues);
    }

    private static void ValidateLimits(FocusDomainBinding binding, string label, List<string> issues)
    {
        var limits = binding.Limits;
        if (limits is null)
        {
            issues.Add($"{label} motion limits are required.");
            return;
        }
        if (limits.MinimumPositionSteps >= limits.MaximumPositionSteps) issues.Add($"{label} position limits are reversed or empty.");
        if (binding.StartPositionSteps < limits.MinimumPositionSteps || binding.StartPositionSteps > limits.MaximumPositionSteps) issues.Add($"{label} start position is outside its allowed range.");
        if (limits.MaximumSingleMoveSteps < 0 || limits.MaximumCumulativeMoveSteps < limits.MaximumSingleMoveSteps) issues.Add($"{label} single/cumulative move limits are invalid.");
        if (limits.MaximumSingleMoveSteps == 0 && limits.MaximumCumulativeMoveSteps != 0) issues.Add($"{label} cumulative motion must also be zero when automatic single moves are disabled.");
        if (limits.MaximumSingleMoveSteps > (long)limits.MaximumPositionSteps - limits.MinimumPositionSteps) issues.Add($"{label} maximum single move exceeds the complete allowed range.");
        if (limits.BacklashCompensationSteps < 0) issues.Add($"{label} backlash compensation cannot be negative.");
        if (limits.BacklashCompensationSteps > limits.MaximumCumulativeMoveSteps) issues.Add($"{label} backlash compensation exceeds the cumulative move limit.");
        if (limits.MaximumSingleMoveSteps == 0 && limits.ApproachDirection != FocusApproachDirection.None) issues.Add($"{label} must use approach direction None when automatic motion is disabled.");
        if (limits.MaximumSingleMoveSteps > 0 && limits.ApproachDirection == FocusApproachDirection.None) issues.Add($"{label} requires an explicit approach direction when automatic motion is allowed.");
    }

    private static void ValidateMetric(NightSetupRecord setup, FocusDomainBinding binding, string label, List<string> issues)
    {
        var metric = binding.Metric;
        if (metric is null)
        {
            issues.Add($"{label} metric evidence is required.");
            return;
        }
        if (string.IsNullOrWhiteSpace(metric.SourceCameraStableDeviceId)) issues.Add($"{label} metric source camera identity is required.");
        if (!double.IsFinite(metric.Value) || metric.Value <= 0) issues.Add($"{label} metric value must be finite and positive.");
        if (string.IsNullOrWhiteSpace(metric.Unit)) issues.Add($"{label} metric unit is required.");
        if (!IsSha256(metric.EvidenceSha256)) issues.Add($"{label} metric requires an explicit evidence SHA-256.");

        var (expectedKind, expectedCameraId) = binding.Role switch
        {
            FocusDomainRole.C11Main => (FocusMetricKind.G3StellarShape, setup.G3StableDeviceId),
            FocusDomainRole.Gs350WideField => (FocusMetricKind.QhyStellarShapeAndPlateSolve, setup.QhyMiniCam8m?.StableDeviceId),
            FocusDomainRole.UvexSpectral => (FocusMetricKind.AtrSpectralLineWidth, setup.Atr585m?.StableDeviceId),
            _ => (metric.Kind, null),
        };
        if (metric.Kind != expectedKind) issues.Add($"{label} metric {metric.Kind} belongs to a different optical path; expected {expectedKind}.");
        if (string.IsNullOrWhiteSpace(expectedCameraId) || !string.Equals(metric.SourceCameraStableDeviceId, expectedCameraId, StringComparison.OrdinalIgnoreCase))
        {
            issues.Add($"{label} metric source does not match the camera assigned to that optical path.");
        }
    }

    private static void ValidateRoleSpecificBinding(FocusDomainBinding binding, string label, List<string> issues)
    {
        if (binding.PhysicalBinding is null) return;
        var physical = binding.PhysicalBinding;
        switch (binding.Role)
        {
            case FocusDomainRole.C11Main:
                RequireIdentity(label, binding.Owner, C11Owner, "owner", issues);
                RequireIdentity(label, binding.LogicalDeviceId, C11LogicalDeviceId, "logical device", issues);
                RequireMechanism(label, physical.Mechanism, FocusMechanism.Gemini, issues);
                RequireIdentity(label, physical.ConnectionEndpoint, C11ConnectionEndpoint, "connection endpoint", issues);
                RequireVidPid(label, physical.HardwareInstanceId, Ch340VidPid, "Gemini/CH340", issues);
                break;
            case FocusDomainRole.Gs350WideField:
                if (string.Equals(binding.Owner, C11Owner, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(binding.Owner, UvexOwner, StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add($"{label} requires an explicit dedicated or manual wide-field owner; the C11 or UVEX owner cannot be substituted.");
                }
                RequireIdentity(label, binding.LogicalDeviceId, Gs350LogicalDeviceId, "logical device", issues);
                RequireMechanism(label, physical.Mechanism, FocusMechanism.ToupTekAaf, issues);
                RequireIdentity(label, physical.ConnectionEndpoint, Gs350ConnectionEndpoint, "connection endpoint", issues);
                RequireVidPid(label, physical.HardwareInstanceId, ToupTekVidPid, "ToupTek AAF", issues);
                if (string.IsNullOrWhiteSpace(physical.TopologyPath)) issues.Add($"{label} must lock the measured USB topology path.");
                break;
            case FocusDomainRole.UvexSpectral:
                RequireIdentity(label, binding.Owner, UvexOwner, "owner", issues);
                RequireIdentity(label, binding.LogicalDeviceId, UvexLogicalDeviceId, "logical device", issues);
                RequireMechanism(label, physical.Mechanism, FocusMechanism.UvexM2, issues);
                RequireIdentity(label, physical.ConnectionEndpoint, UvexConnectionEndpoint, "connection endpoint", issues);
                RequireVidPid(label, physical.HardwareInstanceId, Ch340VidPid, "UVEX4/CH340", issues);
                break;
        }
    }

    private static void RequireIdentity(string label, string actual, string expected, string field, List<string> issues)
    {
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) issues.Add($"{label} {field} must be '{expected}', not '{actual}'.");
    }

    private static void RequireMechanism(string label, FocusMechanism actual, FocusMechanism expected, List<string> issues)
    {
        if (actual != expected) issues.Add($"{label} is assigned mechanism {actual}; expected {expected}.");
    }

    private static void RequireVidPid(string label, string hardwareInstanceId, string expectedVidPid, string device, List<string> issues)
    {
        var normalized = (hardwareInstanceId ?? string.Empty).Replace('/', '\\').ToUpperInvariant();
        if (!normalized.Contains(expectedVidPid, StringComparison.Ordinal)) issues.Add($"{label} physical binding is not the expected {device} VID/PID {expectedVidPid}.");
    }

    private static bool HasSpecificHardwareInstance(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var normalized = value.Replace('/', '\\');
        var segments = normalized.Split('\\', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return segments.Length >= 3 && segments[^1].Length > 0;
    }

    private static bool IsSha256(string value)
    {
        var normalized = (value ?? string.Empty).Replace("-", string.Empty, StringComparison.Ordinal).Trim();
        return normalized.Length == 64 && normalized.All(Uri.IsHexDigit);
    }
}
