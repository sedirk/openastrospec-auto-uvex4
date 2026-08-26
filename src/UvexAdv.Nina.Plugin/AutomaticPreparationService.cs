using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

public sealed record PreparationOptionChoice(string Id, string DisplayName, string Description);

internal sealed record AutomaticPreparationEvidenceReport(
    bool HasStationOperationalProfile,
    int Phd2EvidenceCount,
    int CurrentNightSetupCount,
    int Schema5PresetCount,
    int CompleteBindingsCount,
    IReadOnlyList<string> CurrentNightSetupPaths,
    IReadOnlyList<string> CompleteBindingsPaths)
{
    public bool HasCompleteInstallationCalibration => Schema5PresetCount > 0 && CompleteBindingsCount > 0;
    public bool HasCurrentNightSetup => CurrentNightSetupCount > 0;

    public string InstallationStatus => HasCompleteInstallationCalibration
        ? $"已找到 {CompleteBindingsCount} 份可导入的完整安装标定包（含 schema-5 标定与绑定）。"
        : Phd2EvidenceCount > 0
            ? $"已自动找到 PHD2 不可变证据 {Phd2EvidenceCount} 份，但尚无完整安装标定包。还需由标定流程汇总四槽狭缝 HDR 指纹、三焦域证据、设备身份和运动限额；这些不是要手工抄写的普通参数。"
            : "尚无完整安装标定包，也未找到 PHD2 不可变证据。请导入由设备标定流程生成的锁定包；不要逐项手填哈希或运动限额。";

    public string NightSetupStatus => HasCurrentNightSetup
        ? $"已找到 {CurrentNightSetupCount} 份 schema-2 本夜配置，可直接选择导入；也可以从当前保存值生成新的准备草稿。"
        : "尚无可锁定的 schema-2 本夜配置。可从已保存的设备身份、相机参数、狭缝和下方中文选项一键生成准备草稿；三焦域实测证据到齐后再锁定。";

    public string InventorySummary =>
        $"本机证据：台站运行模板 {(HasStationOperationalProfile ? "已找到" : "缺失")} · PHD2 {Phd2EvidenceCount} 份 · " +
        $"schema-2 本夜配置 {CurrentNightSetupCount} 份 · schema-5 安装标定 {Schema5PresetCount} 份 · 完整绑定包 {CompleteBindingsCount} 份。扫描只读取文件，不连接设备。";
}

internal sealed record NightSetupPreparationDraftInput(
    string TargetName,
    string CatalogId,
    string TelescopeId,
    string AtrCameraId,
    int AtrGain,
    int AtrOffset,
    int AtrBinning,
    int AtrRoiX,
    int AtrRoiY,
    int AtrRoiWidth,
    int AtrRoiHeight,
    double AtrTargetTemperatureC,
    int AtrReadoutMode,
    string G3CameraId,
    string Phd2ProfileName,
    int G3ExposureMilliseconds,
    int G3GainPercent,
    int G3SaturationAdu,
    string QhyCameraId,
    int QhyGain,
    int QhyOffset,
    int QhyBinning,
    int QhyReadoutMode,
    int QhyRoiX,
    int QhyRoiY,
    int QhyRoiWidth,
    int QhyRoiHeight,
    double? QhyTargetTemperatureC,
    int SlitPosition,
    int? GratingPositionSteps,
    int? M2PositionSteps,
    double HorizonMinimumDegrees,
    double HorizonStartMarginDegrees,
    double HorizonContinueMarginDegrees,
    string SpectralRegionPreset,
    string CalibrationReferencePreset,
    string SafetyCapabilityPreset,
    bool LongWavelengthOrderSortingFilterInstalled,
    AutomaticPreparationEvidenceReport Evidence);

internal sealed record NightSetupPreparationDraft(
    string DocumentKind,
    int DraftSchemaVersion,
    DateTimeOffset GeneratedUtc,
    string Status,
    string Notice,
    object Observation,
    object DeviceOwners,
    object CameraSettings,
    object OpticalSetup,
    object OperatorSelections,
    object EvidenceInventory,
    IReadOnlyList<string> UnresolvedItems,
    IReadOnlyList<string> FinalizationSteps);

/// <summary>
/// File-only preparation helper. It never opens cameras, COM5, PHD2 or a mount.
/// A generated draft is deliberately not a NightSetupRecord and therefore cannot
/// be mistaken for locked commissioning evidence.
/// </summary>
internal static class AutomaticPreparationService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AutomaticPreparationEvidenceReport Discover(string programDataRoot)
    {
        var commissioning = Path.Combine(programDataRoot, "commissioning");
        if (!Directory.Exists(commissioning))
        {
            return new(false, 0, 0, 0, 0, [], []);
        }

        var stationProfile = Path.Combine(commissioning, "station-profiles", "default.station-profile.json");
        var phd2Count = 0;
        var nightSetups = new List<string>();
        var schema5Count = 0;
        var bindings = new List<string>();

        foreach (var path in Directory.EnumerateFiles(commissioning, "*.json", SearchOption.AllDirectories))
        {
            try
            {
                if (path.EndsWith(".bindings.json", StringComparison.OrdinalIgnoreCase))
                {
                    if (HasCompleteBindings(path)) bindings.Add(Path.GetFullPath(path));
                    continue;
                }

                var name = Path.GetFileName(path);
                if (name.StartsWith("phd2-profile-", StringComparison.OrdinalIgnoreCase))
                {
                    if (HasValidPhd2EvidenceShape(path)) phd2Count++;
                    continue;
                }

                var bytes = File.ReadAllBytes(path);
                using var document = JsonDocument.Parse(bytes);
                var root = document.RootElement;
                if (JsonInt(root, "SchemaVersion") == 5 && !string.IsNullOrWhiteSpace(JsonString(root, "PresetId")))
                {
                    schema5Count++;
                }

                var setup = JsonSerializer.Deserialize<NightSetupRecord>(bytes, JsonOptions);
                if (setup is not null &&
                    setup.SchemaVersion == NightSetupRecord.CurrentSchemaVersion &&
                    setup.Validate().Count == 0)
                {
                    nightSetups.Add(Path.GetFullPath(path));
                }
            }
            catch
            {
                // Evidence discovery is best effort. Invalid JSON remains invalid
                // and is rejected by the explicit import path.
            }
        }

        return new AutomaticPreparationEvidenceReport(
            File.Exists(stationProfile),
            phd2Count,
            nightSetups.Count,
            schema5Count,
            bindings.Count,
            nightSetups.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            bindings.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public static string WriteDraft(string programDataRoot, NightSetupPreparationDraftInput input, DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        var generatedUtc = now ?? DateTimeOffset.UtcNow;
        var unresolved = BuildUnresolvedItems(input);
        var draft = new NightSetupPreparationDraft(
            "OpenAstroSpec.NightSetupPreparationDraft",
            1,
            generatedUtc,
            unresolved.Count == 0 ? "ReadyForCommissioningFinalization" : "NeedsMeasuredEvidence",
            "这是由已保存配置自动汇总的准备草稿，不是锁定的 NightSetupRecord，也不授予设备运动或无人值守权限。不要手工伪造 SHA-256、焦域证据或运动限额。",
            new { input.TargetName, input.CatalogId },
            new
            {
                Telescope = new { Owner = "N.I.N.A.", StableId = input.TelescopeId },
                Atr585m = new { Owner = "N.I.N.A.", StableId = input.AtrCameraId },
                G3M2210m = new { Owner = "PHD2", StableId = input.G3CameraId, input.Phd2ProfileName },
                QhyMiniCam8m = new { Owner = "QHY acquisition service", StableId = input.QhyCameraId },
                Uvex4 = new { Owner = "UvexAdv.Service", Endpoint = "COM5 only" },
            },
            new
            {
                Atr585m = new
                {
                    input.AtrGain,
                    input.AtrOffset,
                    input.AtrBinning,
                    Roi = new { X = input.AtrRoiX, Y = input.AtrRoiY, Width = input.AtrRoiWidth, Height = input.AtrRoiHeight },
                    input.AtrTargetTemperatureC,
                    input.AtrReadoutMode,
                },
                G3M2210m = new { input.G3ExposureMilliseconds, input.G3GainPercent, input.G3SaturationAdu },
                QhyMiniCam8m = new
                {
                    input.QhyGain,
                    input.QhyOffset,
                    input.QhyBinning,
                    input.QhyReadoutMode,
                    Roi = new { X = input.QhyRoiX, Y = input.QhyRoiY, Width = input.QhyRoiWidth, Height = input.QhyRoiHeight },
                    input.QhyTargetTemperatureC,
                },
            },
            new
            {
                input.SlitPosition,
                input.GratingPositionSteps,
                input.M2PositionSteps,
                Horizon = new { MinimumDegrees = input.HorizonMinimumDegrees, StartMarginDegrees = input.HorizonStartMarginDegrees, ContinueMarginDegrees = input.HorizonContinueMarginDegrees },
            },
            new
            {
                input.SpectralRegionPreset,
                input.CalibrationReferencePreset,
                input.SafetyCapabilityPreset,
                input.LongWavelengthOrderSortingFilterInstalled,
            },
            new
            {
                input.Evidence.HasStationOperationalProfile,
                input.Evidence.Phd2EvidenceCount,
                input.Evidence.CurrentNightSetupCount,
                input.Evidence.Schema5PresetCount,
                input.Evidence.CompleteBindingsCount,
            },
            unresolved,
            [
                "由相应设备所有者只读采集/导入缺失的实测证据。",
                "使用 commissioning 工具生成 schema-2 Night Setup 与 schema-5 commissioning preset，并计算 SHA-256。",
                "导出完整 .bindings.json 后回到 N.I.N.A. 的“自动准备”页一次导入。",
                "启动真实流程时仍须重新回读设备身份、安装纪元、拓扑、位置、安全状态和当前质量证据；安装标定不按日历自动过期。",
            ]);

        var directory = Path.Combine(programDataRoot, "commissioning", "drafts");
        Directory.CreateDirectory(directory);
        var safeTarget = SafeFileToken(input.TargetName);
        var path = Path.Combine(directory, $"{generatedUtc:yyyyMMdd-HHmmss}-{safeTarget}.night-setup-draft.json");
        var temp = path + ".tmp";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(draft, JsonOptions);
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
        File.WriteAllText(path + ".sha256", Convert.ToHexString(SHA256.HashData(bytes)) + Environment.NewLine);
        return Path.GetFullPath(path);
    }

    private static List<string> BuildUnresolvedItems(NightSetupPreparationDraftInput input)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(input.TelescopeId)) missing.Add("赤道仪稳定身份");
        if (string.IsNullOrWhiteSpace(input.AtrCameraId) || input.AtrCameraId.StartsWith("SIM-", StringComparison.OrdinalIgnoreCase)) missing.Add("ATR585M 真实稳定身份");
        if (input.AtrGain < 0) missing.Add("ATR585M 增益");
        if (input.AtrRoiWidth <= 0 || input.AtrRoiHeight <= 0) missing.Add("ATR585M ROI");
        if (string.IsNullOrWhiteSpace(input.G3CameraId) || string.IsNullOrWhiteSpace(input.Phd2ProfileName)) missing.Add("PHD2 / G3 不可变身份");
        if (string.IsNullOrWhiteSpace(input.QhyCameraId) || input.QhyCameraId.StartsWith("SIM-", StringComparison.OrdinalIgnoreCase)) missing.Add("QHYminiCam8M 真实稳定身份");
        if (input.QhyRoiWidth <= 0 || input.QhyRoiHeight <= 0) missing.Add("QHYminiCam8M ROI");
        if (input.SlitPosition is < 1 or > 4) missing.Add("狭缝槽位");
        if (input.GratingPositionSteps is null) missing.Add("光栅实测位置");
        if (input.M2PositionSteps is null) missing.Add("UVEX M2 实测位置");
        if (string.Equals(input.SpectralRegionPreset, "Unspecified", StringComparison.OrdinalIgnoreCase)) missing.Add("目标波段选择");
        if (string.Equals(input.CalibrationReferencePreset, "Unspecified", StringComparison.OrdinalIgnoreCase)) missing.Add("波长标定参考选择");
        if (string.Equals(input.SafetyCapabilityPreset, "Unspecified", StringComparison.OrdinalIgnoreCase)) missing.Add("安全能力选择");
        if (!input.Evidence.HasCompleteInstallationCalibration) missing.Add("schema-5 完整安装标定包（含四槽狭缝 HDR 指纹与运动限额）");
        if (input.Evidence.Phd2EvidenceCount == 0) missing.Add("PHD2 不可变配置证据");
        missing.Add("C11 主镜、GS350 广域镜、UVEX M2 三个独立焦域的当期实测证据");
        return missing.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool HasCompleteBindings(string path)
    {
        try
        {
            var values = CommissioningProfileCatalog.ReadProfileValues(path);
            var required = new[]
            {
                nameof(UvexPluginSettings.CommissioningPresetPath),
                nameof(UvexPluginSettings.CommissioningPresetId),
                nameof(UvexPluginSettings.CommissioningPresetSha256),
                nameof(UvexPluginSettings.CommissioningHardwareFingerprintSha256),
                nameof(UvexPluginSettings.NightSetupSnapshotPath),
                nameof(UvexPluginSettings.NightSetupSnapshotSha256),
                nameof(UvexPluginSettings.Phd2ProfileEvidenceSha256),
            };
            return required.All(values.ContainsKey);
        }
        catch
        {
            return false;
        }
    }

    private static bool HasValidPhd2EvidenceShape(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = document.RootElement;
        var hash = JsonString(root, "Sha256").Replace("-", string.Empty, StringComparison.Ordinal).Trim();
        return JsonInt(root, "ProfileId") is >= 0 &&
               !string.IsNullOrWhiteSpace(JsonString(root, "ProfileName")) &&
               hash.Length == 64 && hash.All(Uri.IsHexDigit);
    }

    private static string SafeFileToken(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var normalized = new string((value ?? string.Empty)
            .Select(ch => invalid.Contains(ch) || char.IsWhiteSpace(ch) ? '-' : ch)
            .ToArray()).Trim('-');
        return string.IsNullOrWhiteSpace(normalized) ? "untitled-target" : normalized[..Math.Min(48, normalized.Length)];
    }

    private static string JsonString(JsonElement parent, string name) =>
        TryGetProperty(parent, name, out var node) && node.ValueKind == JsonValueKind.String
            ? node.GetString() ?? string.Empty
            : string.Empty;

    private static int? JsonInt(JsonElement parent, string name) =>
        TryGetProperty(parent, name, out var node) && node.TryGetInt32(out var value) ? value : null;

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
}
