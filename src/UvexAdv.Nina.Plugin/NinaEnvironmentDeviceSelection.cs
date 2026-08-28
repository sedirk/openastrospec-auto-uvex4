using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

/// <summary>
/// Immutable N.I.N.A. Profile selections for the observatory environment
/// adapters. These adapters do not own ATR, G3, QHY or UVEX4, but their exact
/// selections still affect whether a real run may connect, move or expose.
/// </summary>
internal sealed record NinaEnvironmentDeviceSelection(
    string SafetyMonitorId,
    string DomeOrRoofId,
    string WeatherDataId,
    string OpticalCoverId)
{
    public const string NoDeviceId = "No_Device";

    public static NinaEnvironmentDeviceSelection None { get; } = new(
        NoDeviceId,
        NoDeviceId,
        NoDeviceId,
        NoDeviceId);

    public bool HasSafetyMonitor => IsSelected(SafetyMonitorId);
    public bool HasDomeOrRoof => IsSelected(DomeOrRoofId);
    public bool HasWeatherData => IsSelected(WeatherDataId);
    public bool HasOpticalCover => IsSelected(OpticalCoverId);

    public static bool IsSelected(string? id) =>
        !string.IsNullOrWhiteSpace(id) &&
        !string.Equals(id.Trim(), NoDeviceId, StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(id.Trim(), "No Device", StringComparison.OrdinalIgnoreCase);

    public GateResult ValidateForMode(bool weakSupervisionEnabled)
    {
        var missing = new List<string>();
        if (!HasSafetyMonitor) missing.Add("safety monitor");
        if (!HasDomeOrRoof) missing.Add("dome/roll-off-roof adapter");
        if (!HasWeatherData) missing.Add("weather adapter");
        if (!HasOpticalCover) missing.Add("optical-cover adapter");

        if (missing.Count == 0)
        {
            return GateResult.Pass(
                weakSupervisionEnabled
                    ? "WEAK_SUPERVISION_ALL_ENVIRONMENT_ADAPTERS_SELECTED"
                    : "UNATTENDED_ENVIRONMENT_ADAPTERS_SELECTED",
                "The N.I.N.A. Profile selects all four environment adapters; their live connection and state still require fresh verification.");
        }

        return weakSupervisionEnabled
            ? GateResult.Pass(
                "WEAK_SUPERVISION_ENVIRONMENT_ADAPTERS_DEGRADED",
                $"Weak supervision will continue with warnings because the Profile has no {string.Join(", ", missing)}. " +
                "Only the missing capabilities are degraded; every selected and connected adapter remains authoritative.")
            : GateResult.Unknown(
                "UNATTENDED_ENVIRONMENT_ADAPTER_SELECTION_MISSING",
                $"A full unattended run requires a selected {string.Join(", ", missing)} in the locked N.I.N.A. Profile.");
    }
}
