using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UvexAdv.Observatory;

/// <summary>
/// A detached planning hint, bound to the imported name, catalogue ID and J2000
/// coordinates. It is never fresh image evidence, device authority or a claim
/// that an object is visible in a particular guide camera.
/// </summary>
public sealed record TargetCatalogMetadata(
    string TargetName,
    string? CatalogId,
    double RightAscensionDegrees,
    double DeclinationDegrees,
    string Source,
    string? ObjectType,
    double? VisualMagnitude,
    DateTimeOffset CapturedUtc,
    string? ObjectSubtype = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    TargetCoordinateProvenance? CoordinateProvenance = null);

public sealed record TargetCoordinateProvenance(string Convention, string Source,
    double SourceRightAscensionDegrees, double SourceDeclinationDegrees,
    double SourceJulianEphemerisDay, bool SourceAberrationEnabled,
    double CorrectionArcseconds, string EpochSemantics);

public sealed record TargetCatalogClassification(
    TargetObservabilityClass PreferredClass,
    string Reason,
    bool HasKnownObjectType,
    bool IsUnsupportedObjectType);

public static class TargetCatalogMetadataSerialization
{
    public static string Write(TargetCatalogMetadata? metadata) =>
        metadata is null ? string.Empty : JsonSerializer.Serialize(metadata);

    public static TargetCatalogMetadata? Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var metadata = JsonSerializer.Deserialize<TargetCatalogMetadata>(json);
            return TargetCatalogClassifier.IsValid(metadata) ? metadata : null;
        }
        catch (JsonException) { return null; }
        catch (NotSupportedException) { return null; }
    }
}

/// <summary>
/// Maps explicit catalogue types to a preferred acquisition route. Magnitude is
/// advisory: there is no universal magnitude at which a guide camera stops
/// detecting a star, so unknown/faint stellar detections are decided by frames.
/// </summary>
public static class TargetCatalogClassifier
{
    public static bool IsValid(TargetCatalogMetadata? metadata) =>
        metadata is not null &&
        !string.IsNullOrWhiteSpace(metadata.TargetName) &&
        !string.IsNullOrWhiteSpace(metadata.Source) &&
        double.IsFinite(metadata.RightAscensionDegrees) && metadata.RightAscensionDegrees is >= 0 and < 360 &&
        double.IsFinite(metadata.DeclinationDegrees) && metadata.DeclinationDegrees is >= -90 and <= 90 &&
        metadata.CapturedUtc != default &&
        (metadata.VisualMagnitude is null || double.IsFinite(metadata.VisualMagnitude.Value));

    public static bool IsBoundTo(TargetCatalogMetadata? metadata, EquatorialTarget target) =>
        IsValid(metadata) &&
        string.Equals(metadata!.TargetName.Trim(), target.Name?.Trim() ?? string.Empty, StringComparison.Ordinal) &&
        string.Equals(metadata.CatalogId?.Trim() ?? string.Empty, target.CatalogId?.Trim() ?? string.Empty, StringComparison.Ordinal) &&
        Math.Abs(metadata.RightAscensionDegrees - target.RightAscensionDegrees) <= 1e-7 &&
        Math.Abs(metadata.DeclinationDegrees - target.DeclinationDegrees) <= 1e-7;

    public static TargetCatalogClassification Classify(TargetCatalogMetadata? metadata)
    {
        if (!IsValid(metadata)) return Unknown("未取得与当前目标绑定的星图类型资料");
        var broad = Normalize(metadata!.ObjectType);
        var detailed = Normalize(metadata.ObjectSubtype);
        var type = string.IsNullOrWhiteSpace(detailed) ? broad : detailed;
        var magnitude = metadata.VisualMagnitude is { } value && value is > -40 and < 50
            ? $"；目录 V 星等 {value.ToString("0.##", CultureInfo.InvariantCulture)}（不是导星相机可见性门限）"
            : "；目录 V 星等未知";
        var source = $"星图类型 {metadata.ObjectSubtype ?? metadata.ObjectType ?? "未知"}{magnitude}";

        // Generic Planet includes the Sun in Stellarium; do not let the Sun's
        // specific object-type "star" turn it into an ordinary sidereal target.
        if (IsMovingOrSolar(broad) || IsMovingOrSolar(type))
            return new(TargetObservabilityClass.DirectStellar,
                source + "；太阳系/移动目标需要独立星历与跟踪路线，不能由本固定 J2000 分支自动观测。", true, true);

        if (Matches(type, "planetary nebula", "possible planetary nebula", "protoplanetary nebula", "pn", "行星状星云", "原行星状星云"))
            return new(TargetObservabilityClass.CompactExtended,
                source + "；优先目录中心 / WCS，不要求星云呈圆形恒星核。", true, false);

        if (Matches(type, "quasar", "possible quasar", "qso", "bl lac object", "blazar", "类星体", "蝎虎座bl型天体", "耀变体"))
            return new(TargetObservabilityClass.FaintPointSource,
                source + "；优先目录 / WCS 暗点源定位，是否可见由新帧判断。", true, false);

        if (IsExtended(type))
            return new(TargetObservabilityClass.ExtendedNebula,
                source + "；以计划坐标作为取样位置，不把邻近恒星或亮结节替代为目标。", true, false);

        if (Matches(broad, "star", "恒星") ||
            Matches(type, "star", "double star", "variable star", "pulsating variable star", "symbiotic star", "emission line star", "young stellar object", "nova", "supernova", "恒星", "双星", "变星"))
            return new(TargetObservabilityClass.DirectStellar,
                source + "；优先直接星像复核，过曝时使用 SEP 短帧；双星必须保留主星对应关系，不能自动换成伴星。", true, false);

        return Unknown(source + "；该类型尚无专用映射");
    }

    private static TargetCatalogClassification Unknown(string reason) =>
        new(TargetObservabilityClass.DirectStellar,
            reason + "；先尝试直接星像识别，再由有界新帧证据决定是否使用目录 / WCS 后备。没有推断目标不可见。", false, false);

    private static bool IsMovingOrSolar(string type) => Matches(type,
        "planet", "minorplanet", "minor planet", "dwarf planet", "asteroid", "comet", "satellite", "artificial satellite", "moon", "sun", "solar system", "solarsystem", "meteor", "行星", "小行星", "彗星", "卫星", "月球", "太阳");

    private static bool IsExtended(string type) => Matches(type,
        "galaxy", "active galaxy", "radio galaxy", "interacting galaxy", "part of a galaxy", "cluster of galaxies", "gx", "galaxy cluster",
        "nebula", "dark nebula", "reflection nebula", "bipolar nebula", "emission nebula", "hii region", "supernova remnant", "supernova remnant candidate", "interstellar matter", "molecular cloud",
        "star cluster", "open star cluster", "globular star cluster", "stellar association", "star cloud", "cluster associated with nebulosity", "region of the sky", "oc", "gc", "bn", "dn", "en", "rn", "snr",
        "星系", "星云", "暗星云", "反射星云", "发射星云", "疏散星团", "球状星团", "星团", "超新星遗迹");

    private static bool Matches(string value, params string[] candidates) => candidates.Contains(value, StringComparer.Ordinal);

    private static string Normalize(string? value) => string.Join(' ',
        (value ?? string.Empty).Trim().ToLowerInvariant().Replace('-', ' ').Replace('_', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries));
}
