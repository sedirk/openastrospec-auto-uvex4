using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

/// <summary>
/// Stable N.I.N.A. Profile selections that can cause N.I.N.A. to open a
/// physical device. These values are read before any mediator Connect call.
/// </summary>
internal sealed record NinaProfileOwnerSelection(
    string? CameraId,
    string? TelescopeId,
    string? FocuserId,
    string? FlatDeviceId,
    string? FilterWheelId,
    string? GuiderName);

internal sealed record NinaProfileOwnerExpectation(
    string AtrCameraId,
    string TelescopeId,
    string FocuserId,
    string FlatDeviceId,
    string FilterWheelId,
    string GuiderName,
    bool RequireFlatDevice);

internal static class NinaProfileOwnerPreflight
{
    // Fixed physical owners for this observatory. The ATR and telescope IDs
    // remain run-bound values; these three logical adapters are equally
    // explicit so a Profile change cannot silently select a substitute.
    public const string C11FocuserDeviceId = FocusDomainConventions.C11LogicalDeviceId;
    public const string OpticalCoverDeviceId = "ASCOM.GeminiAutoCover.CoverCalibrator";
    public const string NoPhysicalFilterWheelDeviceId = "No_Device";
    public const string Phd2GuiderName = "PHD2_Single";

    public static GateResult Validate(
        NinaProfileOwnerSelection selected,
        NinaProfileOwnerExpectation expected)
    {
        var failures = new List<string>();
        RequireExact("ATR camera", selected.CameraId, expected.AtrCameraId, failures);
        RequireExact("telescope", selected.TelescopeId, expected.TelescopeId, failures);
        RequireExact("C11 focuser", selected.FocuserId, expected.FocuserId, failures);
        RequireExact("filter wheel", selected.FilterWheelId, expected.FilterWheelId, failures);
        RequireExact("N.I.N.A. guider adapter", selected.GuiderName, expected.GuiderName, failures);
        if (expected.RequireFlatDevice)
        {
            RequireExact("optical cover/flat device", selected.FlatDeviceId, expected.FlatDeviceId, failures);
        }

        return failures.Count == 0
            ? GateResult.Pass(
                "NINA_PROFILE_OWNERS_PREVALIDATED",
                "Every N.I.N.A. Profile device selection matches its stable owner binding before Connect.")
            : GateResult.Fail(
                "NINA_PROFILE_OWNER_MISMATCH",
                $"{string.Join(" ", failures)} No physical Connect was attempted.");
    }

    private static void RequireExact(
        string label,
        string? selected,
        string expected,
        ICollection<string> failures)
    {
        var actual = selected ?? string.Empty;
        var required = expected ?? string.Empty;
        if (string.IsNullOrWhiteSpace(required))
        {
            failures.Add($"Expected {label} stable identity is empty.");
        }
        else if (!string.Equals(actual, required, StringComparison.Ordinal))
        {
            failures.Add($"Profile {label} selection '{actual}' does not match '{required}'.");
        }
    }
}
