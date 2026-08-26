using System.IO;
using System.Text.Json;
using System.Xml.Linq;

namespace UvexAdv.Nina.Plugin;

public sealed record CommissioningProfileChoice(
    string Id,
    string DisplayName,
    string Description,
    string? BindingsPath,
    bool IsAutomatic)
{
    public const string AutomaticSiteProfileId = "df-uvex4-auto-discovery";
}

public sealed record DeviceIdentityChoice(
    string Id,
    string DisplayName,
    string Source,
    int? Phd2ProfileId = null,
    string? Phd2ProfileName = null,
    string? CameraName = null,
    string? MountName = null,
    string? ProfileEvidenceSha256 = null);

internal sealed record CommissioningProfileCatalogResult(
    IReadOnlyList<CommissioningProfileChoice> Profiles,
    IReadOnlyList<DeviceIdentityChoice> Telescopes,
    IReadOnlyList<DeviceIdentityChoice> AtrCameras,
    IReadOnlyList<DeviceIdentityChoice> G3Cameras,
    IReadOnlyList<DeviceIdentityChoice> QhyCameras);

/// <summary>
/// Discovers saved identities without opening a camera, PHD2, COM5 or any other
/// equipment endpoint.  Discovery is deliberately file-only: N.I.N.A. profiles,
/// QHY service configuration and immutable PHD2 evidence remain owned by their
/// respective applications.
/// </summary>
internal static class CommissioningProfileCatalog
{
    private static readonly Guid PluginGuid = UvexPluginSettings.PluginGuid;
    private static readonly string[] FullBindingsMarkers =
    [
        nameof(UvexPluginSettings.CommissioningPresetPath),
        nameof(UvexPluginSettings.CommissioningPresetId),
        nameof(UvexPluginSettings.CommissioningPresetSha256),
        nameof(UvexPluginSettings.NightSetupSnapshotPath),
        nameof(UvexPluginSettings.Phd2ProfileEvidenceSha256),
    ];

    public static CommissioningProfileCatalogResult Discover(
        string ninaProfilesDirectory,
        string programDataRoot,
        string? rememberedBindingsPath = null)
    {
        var operationalProfile = TryReadDefaultOperationalProfile(programDataRoot);
        var profiles = new List<CommissioningProfileChoice>
        {
            new(
                CommissioningProfileChoice.AutomaticSiteProfileId,
                operationalProfile?.DisplayName ?? "默认台站配置（自动发现）",
                operationalProfile?.Description ?? "启动时从各设备所有者保存的配置读取候选；不连接设备，也不伪造标定证据。",
                operationalProfile?.Path,
                true),
        };
        var telescopes = new List<DeviceIdentityChoice>();
        var atr = new List<DeviceIdentityChoice>();
        var g3 = new List<DeviceIdentityChoice>();
        var qhy = new List<DeviceIdentityChoice>();

        DiscoverNinaProfiles(ninaProfilesDirectory, telescopes, atr, qhy);
        DiscoverQhyConfiguration(programDataRoot, qhy);
        DiscoverPhd2Evidence(programDataRoot, g3);
        DiscoverBindings(programDataRoot, profiles);
        if (rememberedBindingsPath is { Length: > 0 } remembered &&
            remembered.EndsWith(".bindings.json", StringComparison.OrdinalIgnoreCase) &&
            File.Exists(remembered))
        {
            AddBindingsProfile(profiles, remembered);
        }

        return new CommissioningProfileCatalogResult(
            DeduplicateProfiles(profiles),
            DeduplicateDevices(telescopes),
            DeduplicateDevices(atr),
            DeduplicateDevices(g3),
            DeduplicateDevices(qhy));
    }

    public static IReadOnlyDictionary<string, JsonElement> ReadProfileValues(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        if (!TryGetProperty(document.RootElement, "NinaProfileValues", out var values) ||
            values.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("所选设备标定方案不含 NinaProfileValues 对象。");
        }

        return values.EnumerateObject().ToDictionary(
            item => item.Name,
            item => item.Value.Clone(),
            StringComparer.OrdinalIgnoreCase);
    }

    internal static bool IsOperationalProfileSetting(string name) => name switch
    {
        nameof(UvexPluginSettings.G3PlateSolveExposurePresetId) or
        nameof(UvexPluginSettings.G3PlateSolveExposureMillisecondsCsv) or
        nameof(UvexPluginSettings.G3MaximumPlateSolveHintOffsetDegrees) or
        nameof(UvexPluginSettings.G3ExposureMilliseconds) or
        nameof(UvexPluginSettings.G3GainPercent) or
        nameof(UvexPluginSettings.G3CameraRecoveryDelayMilliseconds) or
        nameof(UvexPluginSettings.G3WcsMaximumSingleCorrectionArcseconds) or
        nameof(UvexPluginSettings.G3WcsMaximumRadiusArcseconds) or
        nameof(UvexPluginSettings.G3WcsMaximumCumulativeMotionArcseconds) or
        nameof(UvexPluginSettings.G3WcsMaximumCorrectionAttempts) or
        nameof(UvexPluginSettings.G3WcsMaximumCenteringMinutes) or
        nameof(UvexPluginSettings.G3WcsFreshSolveAuthorizationResidualArcseconds) or
        nameof(UvexPluginSettings.G3TargetInsideFieldMarginPixels) or
        nameof(UvexPluginSettings.G3MotionWorstCaseActionSeconds) or
        nameof(UvexPluginSettings.G3MotionPostSlewSettleSeconds) or
        nameof(UvexPluginSettings.BrightTargetMinimumG3ExposureMilliseconds) or
        nameof(UvexPluginSettings.BrightTargetMaximumQhyWcsAgeMinutes) or
        nameof(UvexPluginSettings.BrightTargetMaximumG3FrameAgeMinutes) or
        nameof(UvexPluginSettings.BrightTargetMaximumQhyResidualArcseconds) or
        nameof(UvexPluginSettings.BrightTargetMaximumCatalogMismatchArcseconds) or
        nameof(UvexPluginSettings.G3FocalLengthMillimeters) or
        nameof(UvexPluginSettings.G3PixelSizeMicrometers) or
        nameof(UvexPluginSettings.QhyFocalLengthMillimeters) or
        nameof(UvexPluginSettings.QhyPixelSizeMicrometers) or
        nameof(UvexPluginSettings.QhyCoarseMaximumSingleCorrectionArcseconds) or
        nameof(UvexPluginSettings.QhyCoarseMaximumCumulativeCorrectionArcseconds) or
        nameof(UvexPluginSettings.QhyCoarseMaximumCorrectionAttempts) or
        nameof(UvexPluginSettings.QhyCoarseMaximumCenteringMinutes) or
        nameof(UvexPluginSettings.G3SearchStepArcseconds) or
        nameof(UvexPluginSettings.G3SearchMaximumRadiusArcseconds) or
        nameof(UvexPluginSettings.G3SearchMaximumCumulativeArcseconds) or
        nameof(UvexPluginSettings.G3SearchMaximumAttempts) or
        nameof(UvexPluginSettings.G3SearchMaximumMinutes) or
        nameof(UvexPluginSettings.MaximumSingleCorrectionArcseconds) or
        nameof(UvexPluginSettings.MaximumCumulativeCorrectionArcseconds) or
        nameof(UvexPluginSettings.MaximumCorrectionAttempts) or
        nameof(UvexPluginSettings.MaximumAcquisitionMinutes) or
        nameof(UvexPluginSettings.HorizonMinimumDegrees) or
        nameof(UvexPluginSettings.HorizonStartMarginDegrees) or
        nameof(UvexPluginSettings.HorizonContinueMarginDegrees) or
        nameof(UvexPluginSettings.WideToSlitTransferMode) or
        nameof(UvexPluginSettings.GhostAssistanceMode) => true,
        _ => false,
    };

    /// <summary>
    /// Restores an explicitly selected complete commissioning package.  When
    /// the profile still points at automatic discovery (the first-use
    /// default), prefer the newest complete bindings bundle so a field package
    /// generated after the previous observing night becomes the next startup
    /// default without asking the operator to browse to it again.
    /// </summary>
    public static CommissioningProfileChoice? SelectStartupProfile(
        IReadOnlyList<CommissioningProfileChoice> profiles,
        string? rememberedProfileId,
        bool preferNewestComplete = false)
    {
        ArgumentNullException.ThrowIfNull(profiles);

        var remembered = profiles.FirstOrDefault(item =>
            !item.IsAutomatic &&
            string.Equals(item.Id, rememberedProfileId, StringComparison.OrdinalIgnoreCase));
        if (remembered is not null && !preferNewestComplete) return remembered;

        var newestComplete = profiles
            .Where(item => !item.IsAutomatic && !string.IsNullOrWhiteSpace(item.BindingsPath))
            .Select(item => new
            {
                Profile = item,
                LastWriteUtc = TryGetLastWriteTimeUtc(item.BindingsPath!),
            })
            .OrderByDescending(item => item.LastWriteUtc)
            .ThenBy(item => item.Profile.Id, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.Profile)
            .FirstOrDefault();
        return newestComplete
            ?? remembered
            ?? profiles.FirstOrDefault(item => item.IsAutomatic)
            ?? profiles.FirstOrDefault();
    }

    private static void DiscoverNinaProfiles(
        string directory,
        ICollection<DeviceIdentityChoice> telescopes,
        ICollection<DeviceIdentityChoice> atr,
        ICollection<DeviceIdentityChoice> qhy)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var path in Directory.EnumerateFiles(directory, "*.profile", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var document = XDocument.Load(path, LoadOptions.None);
                var root = document.Root;
                if (root is null) continue;
                var profileName = ChildValue(root, "Name");
                if (string.IsNullOrWhiteSpace(profileName)) profileName = Path.GetFileNameWithoutExtension(path);

                var telescope = Child(root, "TelescopeSettings");
                AddDevice(
                    telescopes,
                    ChildValue(telescope, "Id"),
                    $"{ChildValue(telescope, "LastDeviceName")} · N.I.N.A. 配置“{profileName}”",
                    $"N.I.N.A. 配置：{profileName}");

                var camera = Child(root, "CameraSettings");
                var cameraId = ChildValue(camera, "Id");
                var cameraName = ChildValue(camera, "LastDeviceName");
                var cameraIdentity = $"{cameraId}|{cameraName}";
                if (cameraIdentity.Contains("QHYminiCam8M", StringComparison.OrdinalIgnoreCase))
                {
                    AddDevice(qhy, cameraId, $"{cameraName} · 历史 N.I.N.A. 配置“{profileName}”", $"N.I.N.A. 历史配置：{profileName}");
                }
                else if (cameraIdentity.Contains("ATR585M", StringComparison.OrdinalIgnoreCase))
                {
                    AddDevice(atr, cameraId, $"{cameraName} · N.I.N.A. 配置“{profileName}”", $"N.I.N.A. 配置：{profileName}");
                }

                var pluginValues = ReadPluginValues(root);
                if (pluginValues.TryGetValue(nameof(UvexPluginSettings.BoundCameraId), out var boundAtr) &&
                    !string.IsNullOrWhiteSpace(boundAtr))
                {
                    AddDevice(
                        atr,
                        boundAtr,
                        $"ATR585M · 既有绑定 · N.I.N.A. 配置“{profileName}”",
                        $"OpenAstroSpec 既有绑定：{profileName}");
                }
                if (pluginValues.TryGetValue(nameof(UvexPluginSettings.ObservationExpectedAtrCameraId), out var expectedAtr) &&
                    !expectedAtr.StartsWith("SIM-", StringComparison.OrdinalIgnoreCase))
                {
                    AddDevice(atr, expectedAtr, $"ATR585M · 台站记录 · “{profileName}”", $"OpenAstroSpec 配置：{profileName}");
                }
                if (pluginValues.TryGetValue(nameof(UvexPluginSettings.ObservationExpectedQhyCameraId), out var expectedQhy) &&
                    !expectedQhy.StartsWith("SIM-", StringComparison.OrdinalIgnoreCase))
                {
                    AddDevice(qhy, expectedQhy, $"QHYminiCam8M · 台站记录 · “{profileName}”", $"OpenAstroSpec 配置：{profileName}");
                }
            }
            catch
            {
                // A damaged unrelated N.I.N.A. profile must not make the plugin
                // unusable.  The candidate simply does not appear.
            }
        }
    }

    private static void DiscoverQhyConfiguration(string programDataRoot, ICollection<DeviceIdentityChoice> qhy)
    {
        var path = Path.Combine(programDataRoot, "qhy", "appsettings.json");
        if (!File.Exists(path)) return;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            if (!TryGetProperty(document.RootElement, "Qhy", out var section) ||
                !TryGetProperty(section, "ExpectedStableId", out var idNode) ||
                idNode.ValueKind != JsonValueKind.String) return;
            var id = idNode.GetString() ?? string.Empty;
            if (id.Contains('<', StringComparison.Ordinal) || id.StartsWith("SIM-", StringComparison.OrdinalIgnoreCase)) return;
            AddDevice(qhy, id, "QHYminiCam8M · QHY 服务配置", "QHY 独立采集服务");
        }
        catch
        {
            // Candidate discovery is best effort and never weakens runtime checks.
        }
    }

    private static void DiscoverPhd2Evidence(string programDataRoot, ICollection<DeviceIdentityChoice> g3)
    {
        var directory = Path.Combine(programDataRoot, "commissioning");
        if (!Directory.Exists(directory)) return;
        foreach (var path in Directory.EnumerateFiles(directory, "phd2-profile-*.json", SearchOption.TopDirectoryOnly))
        {
            if (path.EndsWith(".bindings.json", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllBytes(path));
                var root = document.RootElement;
                var profileId = JsonInt(root, "ProfileId");
                var profileName = JsonString(root, "ProfileName");
                var cameraName = JsonString(root, "CameraName");
                var mountName = JsonString(root, "MountName");
                var evidenceHash = JsonString(root, "Sha256");
                if (!TryGetProperty(root, "CameraStableIds", out var ids) || ids.ValueKind != JsonValueKind.Array) continue;
                foreach (var node in ids.EnumerateArray())
                {
                    var id = node.ValueKind == JsonValueKind.String ? node.GetString() ?? string.Empty : string.Empty;
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    g3.Add(new DeviceIdentityChoice(
                        id,
                        $"G3M2210M · PHD2 配置“{profileName}”",
                        $"PHD2 不可变证据：{Path.GetFileName(path)}",
                        profileId,
                        profileName,
                        cameraName,
                        mountName,
                        evidenceHash));
                }
            }
            catch
            {
                // Invalid evidence is ignored here and remains a hard failure if
                // somebody later tries to use it as a locked run binding.
            }
        }
    }

    private static void DiscoverBindings(string programDataRoot, ICollection<CommissioningProfileChoice> profiles)
    {
        var directory = Path.Combine(programDataRoot, "commissioning");
        if (!Directory.Exists(directory)) return;
        foreach (var path in Directory.EnumerateFiles(directory, "*.bindings.json", SearchOption.AllDirectories))
        {
            AddBindingsProfile(profiles, path);
        }
    }

    private static void AddBindingsProfile(ICollection<CommissioningProfileChoice> profiles, string path)
    {
        try
        {
            var values = ReadProfileValues(path);
            if (FullBindingsMarkers.Any(marker => !values.ContainsKey(marker))) return;
            var presetId = JsonElementString(values, nameof(UvexPluginSettings.CommissioningPresetId));
            var setupId = JsonElementString(values, nameof(UvexPluginSettings.ObservationNightSetupId));
            var label = !string.IsNullOrWhiteSpace(presetId)
                ? presetId
                : !string.IsNullOrWhiteSpace(setupId) ? setupId : Path.GetFileNameWithoutExtension(path);
            profiles.Add(new CommissioningProfileChoice(
                Path.GetFullPath(path),
                $"设备标定方案 · {label}",
                $"经文件导入：{Path.GetFileName(path)}",
                Path.GetFullPath(path),
                false));
        }
        catch
        {
            // PHD2-only *.bindings.json files intentionally do not contain the
            // complete N.I.N.A. value map and therefore are not station profiles.
        }
    }

    private static OperationalProfileMetadata? TryReadDefaultOperationalProfile(string programDataRoot)
    {
        var path = Path.Combine(
            programDataRoot,
            "commissioning",
            "station-profiles",
            "default.station-profile.json");
        if (!File.Exists(path)) return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = document.RootElement;
            if (!string.Equals(JsonString(root, "ProfileKind"), "OperationalTemplate", StringComparison.OrdinalIgnoreCase) ||
                JsonInt(root, "SchemaVersion") != 1 ||
                !TryGetProperty(root, "NinaProfileValues", out var values) ||
                values.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            var displayName = JsonString(root, "DisplayName");
            var description = JsonString(root, "Description");
            return new OperationalProfileMetadata(
                Path.GetFullPath(path),
                string.IsNullOrWhiteSpace(displayName) ? "默认台站配置（本机）" : displayName,
                string.IsNullOrWhiteSpace(description)
                    ? "启动时读取本机台站运行模板及各设备所有者保存的身份候选；不连接设备。"
                    : description);
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyDictionary<string, string> ReadPluginValues(XElement root)
    {
        var guid = PluginGuid.ToString("D");
        var entry = root.Descendants().FirstOrDefault(node =>
            node.Name.LocalName.StartsWith("KeyValueOfguid", StringComparison.Ordinal) &&
            node.Elements().Any(child =>
                child.Name.LocalName == "Key" &&
                string.Equals(child.Value.Trim(), guid, StringComparison.OrdinalIgnoreCase)));
        var value = entry?.Elements().FirstOrDefault(child => child.Name.LocalName == "Value");
        if (value is null) return new Dictionary<string, string>();
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in value.Elements())
        {
            var key = pair.Elements().FirstOrDefault(child => child.Name.LocalName == "Key")?.Value;
            var item = pair.Elements().FirstOrDefault(child => child.Name.LocalName == "Value")?.Value;
            if (!string.IsNullOrWhiteSpace(key)) result[key] = item ?? string.Empty;
        }
        return result;
    }

    private static XElement? Child(XElement? parent, string localName) =>
        parent?.Elements().FirstOrDefault(item => item.Name.LocalName == localName);

    private static string ChildValue(XElement? parent, string localName) => Child(parent, localName)?.Value.Trim() ?? string.Empty;

    private static void AddDevice(ICollection<DeviceIdentityChoice> target, string id, string display, string source)
    {
        if (string.IsNullOrWhiteSpace(id) || string.Equals(id, "No_Device", StringComparison.OrdinalIgnoreCase)) return;
        target.Add(new DeviceIdentityChoice(id.Trim(), string.IsNullOrWhiteSpace(display) ? id.Trim() : display.Trim(), source));
    }

    private static IReadOnlyList<DeviceIdentityChoice> DeduplicateDevices(IEnumerable<DeviceIdentityChoice> source) =>
        source.GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(item => item.Phd2ProfileId.HasValue).First())
            .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static IReadOnlyList<CommissioningProfileChoice> DeduplicateProfiles(IEnumerable<CommissioningProfileChoice> source) =>
        source.GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderByDescending(item => item.IsAutomatic)
            .ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static DateTime TryGetLastWriteTimeUtc(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch { return DateTime.MinValue; }
    }

    private static string JsonString(JsonElement parent, string name) =>
        TryGetProperty(parent, name, out var node) && node.ValueKind == JsonValueKind.String
            ? node.GetString() ?? string.Empty
            : string.Empty;

    private static int? JsonInt(JsonElement parent, string name) =>
        TryGetProperty(parent, name, out var node) && node.TryGetInt32(out var value) ? value : null;

    private static string JsonElementString(IReadOnlyDictionary<string, JsonElement> values, string name) =>
        values.TryGetValue(name, out var node) && node.ValueKind == JsonValueKind.String
            ? node.GetString() ?? string.Empty
            : string.Empty;

    private static bool TryGetProperty(JsonElement parent, string name, out JsonElement value)
    {
        if (parent.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in parent.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }

    private sealed record OperationalProfileMetadata(string Path, string DisplayName, string Description);
}
