using NINA.Profile.Interfaces;

namespace UvexAdv.Nina.Plugin;

public enum NinaInstanceRole { SpectroscopyMaster, PhotometryWorker }

internal static class NinaInstancePolicy
{
    internal const string WorkerAdapter = "nina-photometry-worker";

    public static bool IsMaster(UvexPluginSettings settings) =>
        settings.InstanceRole == NinaInstanceRole.SpectroscopyMaster;

    public static void RequireMaster(UvexPluginSettings settings)
    {
        if (!IsMaster(settings)) throw new InvalidOperationException(
            "PHOTOMETRY_ROLE_FORBIDDEN: 测光实例不能执行光谱、UVEX、赤道仪、导星或全台控制。 / Photometry worker cannot control shared or spectroscopy equipment.");
    }

    public static void ValidateWorkerProfile(IProfileService profiles, UvexPluginSettings settings)
    {
        if (settings.InstanceRole != NinaInstanceRole.PhotometryWorker)
            throw new InvalidOperationException("PHOTOMETRY_ROLE_REQUIRED: Select the PhotometryWorker profile role first.");
        var p = profiles.ActiveProfile;
        if (p.FilterWheelSettings.DisableGuidingOnFilterChange)
            throw new InvalidOperationException("PHOTOMETRY_SHARED_TRIGGER_FORBIDDEN: Disable 'stop guiding on filter change' in the worker Profile; the worker must never manage guiding.");
        ValidateForbiddenSelections(new Dictionary<string, string?>
        {
            ["Telescope"] = p.TelescopeSettings.Id,
            ["Guider"] = p.GuiderSettings.GuiderName,
            ["Dome"] = p.DomeSettings.Id,
            ["Weather"] = p.WeatherDataSettings.Id,
            ["SafetyMonitor"] = p.SafetyMonitorSettings.Id,
            ["FlatDevice/Cover"] = p.FlatDeviceSettings.Id,
            ["Rotator"] = p.RotatorSettings.Id,
            ["Switch"] = p.SwitchSettings.Id,
        });
    }

    internal static void ValidateForbiddenSelections(IReadOnlyDictionary<string, string?> selections)
    {
        var forbidden = selections.Where(pair => !IsUnselected(pair.Value)).Select(pair => pair.Key).ToArray();
        if (forbidden.Length != 0) throw new InvalidOperationException(
            "PHOTOMETRY_SHARED_DEVICE_FORBIDDEN: 测光 Profile 必须取消这些设备 / Deselect shared equipment: " + string.Join(", ", forbidden));
    }

    private static bool IsUnselected(string? id) => string.IsNullOrWhiteSpace(id) ||
        string.Equals(id, "No_Device", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(id, "No_Guider", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(id, "No Guider", StringComparison.OrdinalIgnoreCase);

    public static bool TryWorkerEndpoint(string? address, out Guid profileId)
    {
        profileId = default;
        return Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme == "nina" &&
            string.IsNullOrEmpty(uri.UserInfo) && uri.IsDefaultPort &&
            string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment) &&
            uri.AbsolutePath is "" or "/" && Guid.TryParse(uri.Host, out profileId) && profileId != Guid.Empty;
    }

    public static string WorkerPipeName(Guid profileId) => $"OpenAstroSpec.Photometry.v1.{profileId:N}";
}
