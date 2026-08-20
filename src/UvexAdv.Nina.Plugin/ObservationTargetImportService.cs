using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace UvexAdv.Nina.Plugin;

/// <summary>
/// Immutable J2000 coordinates copied from an external planning surface.
/// Right ascension is always expressed in degrees in the half-open range [0, 360).
/// </summary>
public sealed record ObservationTargetCoordinates(
    double RightAscensionDegrees,
    double DeclinationDegrees);

/// <summary>
/// A detached target snapshot. It deliberately contains no run mode, equipment,
/// commissioning, duration, safety, or motion settings.
/// </summary>
public sealed record ObservationTargetImportResult(
    string TargetName,
    string CatalogId,
    double RightAscensionDegrees,
    double DeclinationDegrees,
    string Source,
    DateTimeOffset ImportedUtc,
    string Epoch,
    double? PositionAngleDegrees,
    string Details,
    ObservationTargetCoordinates ImportedCoordinates,
    ObservationTargetCoordinates? InitialFramingCoordinates,
    ObservationTargetCoordinates? FramingCenterCoordinates,
    bool UsedFramingCenter);

/// <summary>
/// The complete state copied from the framing assistant at one instant.
/// The service validates this detached value rather than continuing to observe
/// the mutable framing-assistant view model.
/// </summary>
public sealed record ObservationFramingTargetSnapshot(
    bool HasDeepSkyObject,
    string? TargetName,
    string? CatalogId,
    string? FramingRectangleTargetName,
    ObservationTargetCoordinates? DeepSkyObjectCoordinates,
    ObservationTargetCoordinates? InitialFramingCoordinates,
    ObservationTargetCoordinates? FramingCenterCoordinates,
    int HorizontalPanels,
    int VerticalPanels,
    int FramingRectangleCount,
    bool FramingCenterCalculated,
    double? PositionAngleDegrees,
    string SourceName,
    string? SourceDetails = null);

/// <summary>
/// The complete state copied from N.I.N.A.'s configured planetarium at one instant.
/// </summary>
public sealed record ObservationPlanetariumTargetSnapshot(
    string? TargetName,
    string? CatalogId,
    ObservationTargetCoordinates? TargetBodyCoordinates,
    double? PositionAngleDegrees,
    string SourceName,
    string? SourceDetails = null);

public interface IObservationFramingTargetSource
{
    ObservationFramingTargetSnapshot Capture();
}

public interface IObservationPlanetariumTargetSource
{
    Task<ObservationPlanetariumTargetSnapshot> CaptureAsync(CancellationToken cancellationToken);
}

public sealed class ObservationTargetImportException : Exception
{
    public ObservationTargetImportException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}

/// <summary>
/// Converts one explicitly requested external target snapshot into the UVEX plan's
/// target fields. Import is read-only with respect to N.I.N.A. and all equipment.
/// </summary>
public sealed class ObservationTargetImportService
{
    public const string J2000Epoch = "J2000";

    private readonly IObservationFramingTargetSource framingSource;
    private readonly IObservationPlanetariumTargetSource planetariumSource;
    private readonly Func<DateTimeOffset> utcNow;

    public ObservationTargetImportService(
        IObservationFramingTargetSource framingSource,
        IObservationPlanetariumTargetSource planetariumSource,
        Func<DateTimeOffset>? utcNow = null)
    {
        this.framingSource = framingSource ?? throw new ArgumentNullException(nameof(framingSource));
        this.planetariumSource = planetariumSource ?? throw new ArgumentNullException(nameof(planetariumSource));
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Copies the current framing-assistant state once. The framing rectangle's
    /// initial center is the default; the current, possibly moved center is selected
    /// only by passing <paramref name="useFramingCenter"/>.
    /// </summary>
    public ObservationTargetImportResult ImportFromFramingAssistant(bool useFramingCenter = false)
    {
        ObservationFramingTargetSnapshot snapshot;
        try
        {
            snapshot = framingSource.Capture();
        }
        catch (ObservationTargetImportException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ObservationTargetImportException(
                "FRAMING_SNAPSHOT_FAILED",
                $"无法读取 N.I.N.A. 构图助手的当前目标：{ObservationTargetImportErrors.SafeMessage(ex)}",
                ex);
        }

        if (!snapshot.HasDeepSkyObject)
        {
            throw new ObservationTargetImportException(
                "FRAMING_NO_DSO",
                "构图助手中没有命名天体。请先在构图助手中选择一个目标，再导入。自由空域中心不能静默冒充光谱目标。");
        }

        var targetName = RequireName(
            snapshot.TargetName,
            "FRAMING_UNNAMED_DSO",
            "构图助手中的目标没有名称。UVEX 只能导入可明确识别的命名天体。");

        if (snapshot.HorizontalPanels > 1 ||
            snapshot.VerticalPanels > 1 ||
            snapshot.FramingRectangleCount > 1)
        {
            throw new ObservationTargetImportException(
                "FRAMING_MULTI_PANEL_UNSUPPORTED",
                $"当前构图是多面板拼接（{Math.Max(1, snapshot.HorizontalPanels)} × {Math.Max(1, snapshot.VerticalPanels)}，"
                + $"已生成 {Math.Max(0, snapshot.FramingRectangleCount)} 个面板）。请先明确选择单个面板或切回单画幅；UVEX 不会静默取第一个面板。");
        }
        if (snapshot.FramingRectangleCount != 1)
        {
            throw new ObservationTargetImportException(
                "FRAMING_RECTANGLE_UNAVAILABLE",
                "构图助手尚未生成唯一的单画幅构图矩形。请等待当前目标图像与构图矩形加载完成后再导入。");
        }

        var initialFraming = NormalizeCoordinates(
            snapshot.InitialFramingCoordinates,
            "FRAMING_INITIAL_COORDINATES_MISSING",
            "构图助手没有保留本次构图矩形的有效初始中心，无法把当前拖动位置冒充为原始目标位置。请重新载入该命名目标后再导入。");
        var framingCenter = snapshot.FramingCenterCoordinates is null
            ? null
            : NormalizeCoordinates(
                snapshot.FramingCenterCoordinates,
                "FRAMING_CENTER_COORDINATES_INVALID",
                "构图助手的单画幅中心坐标无效，无法导入。");
        var deepSkyObjectCoordinates = NormalizeCoordinates(
            snapshot.DeepSkyObjectCoordinates,
            "FRAMING_DSO_COORDINATES_MISSING",
            "构图助手的命名目标没有可核验坐标。请等待当前目标及构图矩形加载完成后重试。");

        if (useFramingCenter && framingCenter is null)
        {
            throw new ObservationTargetImportException(
                "FRAMING_CENTER_UNAVAILABLE",
                "构图助手尚未提供可用的单画幅中心。请先生成单画幅构图，或改用默认的本次构图矩形初始中心。");
        }

        if (useFramingCenter && !snapshot.FramingCenterCalculated)
        {
            throw new ObservationTargetImportException(
                "FRAMING_CENTER_NOT_CURRENT",
                "构图助手的单画幅中心尚未计算或尚未刷新。为避免导入陈旧坐标，请先完成构图刷新，再显式导入构图中心。");
        }

        if (!string.Equals(snapshot.FramingRectangleTargetName, targetName, StringComparison.Ordinal) ||
            !snapshot.FramingCenterCalculated || framingCenter is null ||
            AngularSeparationArcSeconds(deepSkyObjectCoordinates, framingCenter) > 1d)
        {
            throw new ObservationTargetImportException(
                "FRAMING_TARGET_RECTANGLE_STALE",
                "构图助手的命名目标与当前构图矩形不是同一代快照。N.I.N.A. 可能仍在加载新目标；请等待图像和构图矩形刷新完成后再导入。");
        }

        var selected = useFramingCenter ? framingCenter! : initialFraming;
        var sourceName = NormalizeSourceName(snapshot.SourceName, "N.I.N.A. 构图助手");
        var source = $"{sourceName} / {(useFramingCenter ? "当前构图中心" : "本次构图矩形初始中心")}";
        var details = BuildFramingDetails(initialFraming, framingCenter, useFramingCenter, snapshot.SourceDetails);

        return new ObservationTargetImportResult(
            targetName,
            string.Empty,
            selected.RightAscensionDegrees,
            selected.DeclinationDegrees,
            source,
            utcNow().ToUniversalTime(),
            J2000Epoch,
            NormalizePositionAngle(snapshot.PositionAngleDegrees),
            details,
            selected,
            initialFraming,
            framingCenter,
            useFramingCenter);
    }

    /// <summary>
    /// Asks N.I.N.A.'s configured planetarium for its explicitly selected object once.
    /// No polling or continuous binding is retained after this method returns.
    /// </summary>
    public async Task<ObservationTargetImportResult> ImportFromPlanetariumAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        ObservationPlanetariumTargetSnapshot snapshot;
        try
        {
            snapshot = await planetariumSource.CaptureAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw ObservationTargetImportErrors.ForPlanetarium(ex);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ObservationTargetImportException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw ObservationTargetImportErrors.ForPlanetarium(ex);
        }

        cancellationToken.ThrowIfCancellationRequested();

        var targetName = RequireName(
            snapshot.TargetName,
            "PLANETARIUM_NO_NAMED_TARGET",
            "第三方星图中没有明确选中的命名目标。请先在 Stellarium 中点击目标，再重试。");
        var targetBody = NormalizeCoordinates(
            snapshot.TargetBodyCoordinates,
            "PLANETARIUM_COORDINATES_MISSING",
            "第三方星图所选目标没有有效坐标。请重新选择一个命名目标后重试。");
        var sourceName = NormalizeSourceName(snapshot.SourceName, "N.I.N.A. 第三方星图");
        var details = $"已从 {sourceName} 复制一次当前选择；目标 J2000 {FormatCoordinates(targetBody)}。"
            + "N.I.N.A. 当前公开星图接口没有提供可与名称独立核验的目录号，本次已清空旧目录 ID。"
            + "导入仅修改目标草稿，不会连接设备、移动赤道仪或启动观测。"
            + AppendSourceDetails(snapshot.SourceDetails);

        return new ObservationTargetImportResult(
            targetName,
            string.Empty,
            targetBody.RightAscensionDegrees,
            targetBody.DeclinationDegrees,
            sourceName,
            utcNow().ToUniversalTime(),
            J2000Epoch,
            NormalizePositionAngle(snapshot.PositionAngleDegrees),
            details,
            targetBody,
            null,
            null,
            false);
    }

    private static string BuildFramingDetails(
        ObservationTargetCoordinates initialFraming,
        ObservationTargetCoordinates? framingCenter,
        bool useFramingCenter,
        string? sourceDetails)
    {
        var selectedLabel = useFramingCenter ? "当前构图中心" : "本次构图矩形初始中心";
        string coordinateComparison;
        if (framingCenter is null)
        {
            coordinateComparison = $"本次构图矩形初始中心 J2000 {FormatCoordinates(initialFraming)}；当前没有可用的单画幅构图中心。";
        }
        else
        {
            var separationArcSeconds = AngularSeparationArcSeconds(initialFraming, framingCenter);
            coordinateComparison = $"本次构图矩形初始中心 J2000 {FormatCoordinates(initialFraming)}；"
                + $"当前构图中心 J2000 {FormatCoordinates(framingCenter)}；"
                + $"二者相差 {separationArcSeconds.ToString("F2", CultureInfo.InvariantCulture)} 角秒。";
        }

        return coordinateComparison
            + $"本次显式采用{selectedLabel}；默认导入采用本次矩形的初始中心，只有操作员选择当前构图中心时才会改用拖动后的中心。"
            + "N.I.N.A. 当前公开接口没有提供可与名称独立核验的目录号，本次已清空旧目录 ID。"
            + "导入仅修改目标草稿，不会连接设备、移动赤道仪或启动观测。"
            + AppendSourceDetails(sourceDetails);
    }

    private static string AppendSourceDetails(string? sourceDetails) =>
        string.IsNullOrWhiteSpace(sourceDetails) ? string.Empty : $" {sourceDetails.Trim()}";

    private static ObservationTargetCoordinates NormalizeCoordinates(
        ObservationTargetCoordinates? coordinates,
        string missingCode,
        string missingMessage)
    {
        if (coordinates is null)
        {
            throw new ObservationTargetImportException(missingCode, missingMessage);
        }

        var ra = coordinates.RightAscensionDegrees;
        var dec = coordinates.DeclinationDegrees;
        if (!double.IsFinite(ra) || !double.IsFinite(dec) ||
            ra is < 0d or >= 360d || dec is < -90d or > 90d)
        {
            throw new ObservationTargetImportException(
                "TARGET_COORDINATES_INVALID",
                $"导入坐标无效：RA={ra.ToString("R", CultureInfo.InvariantCulture)}°，"
                + $"Dec={dec.ToString("R", CultureInfo.InvariantCulture)}°。RA 必须位于 0°（含）至 360°（不含），Dec 必须位于 −90° 至 +90°。");
        }

        return new ObservationTargetCoordinates(ra, dec);
    }

    private static double? NormalizePositionAngle(double? positionAngleDegrees)
    {
        if (positionAngleDegrees is null || !double.IsFinite(positionAngleDegrees.Value))
        {
            return null;
        }

        var normalized = positionAngleDegrees.Value % 360d;
        return normalized < 0d ? normalized + 360d : normalized;
    }

    private static string RequireName(string? value, string code, string message)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ObservationTargetImportException(code, message);
        }

        return value.Trim();
    }

    private static string NormalizeSourceName(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string FormatCoordinates(ObservationTargetCoordinates coordinates) =>
        $"RA {coordinates.RightAscensionDegrees.ToString("F8", CultureInfo.InvariantCulture)}°，"
        + $"Dec {coordinates.DeclinationDegrees.ToString("+0.00000000;-0.00000000;0.00000000", CultureInfo.InvariantCulture)}°";

    private static double AngularSeparationArcSeconds(
        ObservationTargetCoordinates left,
        ObservationTargetCoordinates right)
    {
        const double degreesToRadians = Math.PI / 180d;
        var leftDec = left.DeclinationDegrees * degreesToRadians;
        var rightDec = right.DeclinationDegrees * degreesToRadians;
        var raDifference = (right.RightAscensionDegrees - left.RightAscensionDegrees) * degreesToRadians;
        var cosine = (Math.Sin(leftDec) * Math.Sin(rightDec))
            + (Math.Cos(leftDec) * Math.Cos(rightDec) * Math.Cos(raDifference));
        var radians = Math.Acos(Math.Clamp(cosine, -1d, 1d));
        return radians / degreesToRadians * 3600d;
    }
}

public static class ObservationTargetImportErrors
{
    public static ObservationTargetImportException ForPlanetarium(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (LooksLikeNoSelectedObject(exception))
        {
            return new ObservationTargetImportException(
                "PLANETARIUM_NO_SELECTED_OBJECT",
                "第三方星图中没有明确选中的命名目标。请先在 Stellarium 中点击目标，再重试。",
                exception);
        }

        if (Flatten(exception).Any(current =>
                current is HttpRequestException { StatusCode: null } or
                SocketException or
                TimeoutException or
                TaskCanceledException))
        {
            return new ObservationTargetImportException(
                "PLANETARIUM_CONNECTION_FAILED",
                "无法连接 N.I.N.A. 当前配置的第三方星图，或读取请求已超时。请确认 Stellarium 与 Remote Control 已启动，并核对主机和端口。",
                exception);
        }

        var typeName = exception.GetType().Name;
        if (typeName.Contains("FailedToConnect", StringComparison.OrdinalIgnoreCase))
        {
            return new ObservationTargetImportException(
                "PLANETARIUM_CONNECTION_FAILED",
                "无法连接 N.I.N.A. 当前配置的第三方星图。请确认 Stellarium 与 Remote Control 已启动，并核对主机和端口。",
                exception);
        }

        if (typeName.Contains("FailedToGetCoordinates", StringComparison.OrdinalIgnoreCase))
        {
            return new ObservationTargetImportException(
                "PLANETARIUM_COORDINATES_FAILED",
                "N.I.N.A. 已连接第三方星图，但无法读取所选目标的坐标。请重新选择一个命名目标后重试。",
                exception);
        }

        return new ObservationTargetImportException(
            "PLANETARIUM_SNAPSHOT_FAILED",
            $"读取 N.I.N.A. 第三方星图当前目标失败：{SafeMessage(exception)}",
            exception);
    }

    public static bool LooksLikeNoSelectedObject(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        foreach (var current in Flatten(exception))
        {
            if (current is HttpRequestException { StatusCode: HttpStatusCode.NotFound })
            {
                return true;
            }

            if (current is WebException { Response: HttpWebResponse response } &&
                response.StatusCode == HttpStatusCode.NotFound)
            {
                return true;
            }

            var typeName = current.GetType().Name;
            if (typeName.Contains("ObjectNotSelected", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var message = current.Message;
            if (message.Contains("404", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("no object selected", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("object not selected", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static string SafeMessage(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var message = exception.Message?.Trim();
        return string.IsNullOrWhiteSpace(message) ? exception.GetType().Name : message;
    }

    private static IEnumerable<Exception> Flatten(Exception exception)
    {
        var pending = new Stack<Exception>();
        pending.Push(exception);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            yield return current;

            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    pending.Push(inner);
                }
            }
            else if (current.InnerException is not null)
            {
                pending.Push(current.InnerException);
            }
        }
    }
}
