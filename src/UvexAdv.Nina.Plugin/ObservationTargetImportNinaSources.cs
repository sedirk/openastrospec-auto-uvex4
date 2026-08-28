using NINA.Astrometry;
using NINA.Equipment.Interfaces;
using NINA.WPF.Base.Interfaces.ViewModel;
using System.Net.Http;
using System.Text.Json;

namespace UvexAdv.Nina.Plugin;

/// <summary>
/// Creates target import sources over N.I.N.A. 3.2's public planning interfaces.
/// These adapters only read planning state; they have no equipment mediators.
/// </summary>
public static class ObservationTargetImportNinaSources
{
    public static ObservationTargetImportService CreateService(
        IFramingAssistantVM framingAssistant,
        IPlanetariumFactory planetariumFactory,
        Func<Uri?>? stellariumEndpoint = null,
        Func<DateTimeOffset>? utcNow = null) =>
        new(
            new NinaFramingAssistantTargetSource(framingAssistant),
            new NinaPlanetariumTargetSource(planetariumFactory, stellariumEndpoint),
            utcNow);
}

public sealed class NinaFramingAssistantTargetSource : IObservationFramingTargetSource
{
    private readonly IFramingAssistantVM framingAssistant;

    public NinaFramingAssistantTargetSource(IFramingAssistantVM framingAssistant)
    {
        this.framingAssistant = framingAssistant ?? throw new ArgumentNullException(nameof(framingAssistant));
    }

    public ObservationFramingTargetSnapshot Capture()
    {
        // Read every mutable view-model property into local values before converting.
        // The returned records do not retain N.I.N.A. objects and therefore cannot
        // change if the operator moves the framing rectangle after clicking import.
        var dso = framingAssistant.DSO;
        var rectangles = framingAssistant.CameraRectangles?.ToArray() ?? [];
        var horizontalPanels = framingAssistant.HorizontalPanels;
        var verticalPanels = framingAssistant.VerticalPanels;
        var rectangle = rectangles.Length == 1 ? rectangles[0] : null;
        var rectangleCalculated = framingAssistant.RectangleCalculated;

        // FramingAssistantVM updates DSO.Coordinates when the rectangle is dragged.
        // FramingRectangle.OriginalCoordinates is the initial center of this framing
        // rectangle; it is not claimed to be an immutable catalog coordinate. Never
        // fall back to the overwritten DSO.Coordinates and call it a target position.
        var targetBodyCoordinates = rectangle?.OriginalCoordinates;
        var deepSkyObjectCoordinates = dso?.Coordinates is null
            ? null
            : ToJ2000(dso.Coordinates, "构图助手命名目标");
        var targetCoordinates = targetBodyCoordinates is null
            ? null
            : ToJ2000(targetBodyCoordinates, "本次构图矩形初始中心");
        var centerCoordinates = rectangle?.Coordinates is null
            ? null
            : ToJ2000(rectangle.Coordinates, "构图助手构图中心");
        var positionAngle = rectangle?.DSOPositionAngle;

        return new ObservationFramingTargetSnapshot(
            dso is not null,
            dso?.Name,
            dso?.Id,
            rectangle?.Name,
            deepSkyObjectCoordinates,
            targetCoordinates,
            centerCoordinates,
            horizontalPanels,
            verticalPanels,
            rectangles.Length,
            rectangleCalculated,
            positionAngle,
            "N.I.N.A. 构图助手",
            $"读取时单画幅构图状态：{(rectangleCalculated ? "已计算" : "尚未计算或尚未刷新")}。");
    }

    private static ObservationTargetCoordinates ToJ2000(Coordinates coordinates, string label)
    {
        try
        {
            var j2000 = coordinates.Epoch == Epoch.J2000
                ? coordinates
                : coordinates.Transform(Epoch.J2000);
            return new ObservationTargetCoordinates(j2000.RADegrees, j2000.Dec);
        }
        catch (Exception ex)
        {
            throw new ObservationTargetImportException(
                "TARGET_EPOCH_CONVERSION_FAILED",
                $"{label}无法转换为 J2000 坐标：{ObservationTargetImportErrors.SafeMessage(ex)}",
                ex);
        }
    }
}

public sealed class NinaPlanetariumTargetSource : IObservationPlanetariumTargetSource
{
    private static readonly HttpClient StellariumClient = new()
    {
        Timeout = TimeSpan.FromSeconds(2),
    };

    private readonly IPlanetariumFactory planetariumFactory;
    private readonly Func<Uri?>? stellariumEndpoint;

    public NinaPlanetariumTargetSource(IPlanetariumFactory planetariumFactory)
        : this(planetariumFactory, null)
    {
    }

    internal NinaPlanetariumTargetSource(
        IPlanetariumFactory planetariumFactory,
        Func<Uri?>? stellariumEndpoint)
    {
        this.planetariumFactory = planetariumFactory ?? throw new ArgumentNullException(nameof(planetariumFactory));
        this.stellariumEndpoint = stellariumEndpoint;
    }

    public async Task<ObservationPlanetariumTargetSnapshot> CaptureAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IPlanetarium planetarium;
        try
        {
            planetarium = planetariumFactory.GetPlanetarium()
                ?? throw new ObservationTargetImportException(
                    "PLANETARIUM_NOT_CONFIGURED",
                    "N.I.N.A. 尚未配置第三方星图。请先在 N.I.N.A. 设置中选择 Stellarium，并核对主机和端口。");
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

        DeepSkyObject? target;
        try
        {
            // N.I.N.A. 3.2's IPlanetarium.GetTarget() has no CancellationToken.
            // We therefore check cancellation immediately before and after the
            // single read, but cannot interrupt its in-flight HTTP request.
            cancellationToken.ThrowIfCancellationRequested();
            target = await planetarium.GetTarget().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw ObservationTargetImportErrors.ForPlanetarium(ex);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw ObservationTargetImportErrors.ForPlanetarium(ex);
        }

        var targetCoordinates = target?.Coordinates is null
            ? null
            : ToJ2000(target.Coordinates, planetarium.Name);
        var importedName = target?.Name;
        var importedCatalogId = target?.Id;
        string? identityNote = null;
        if (targetCoordinates is not null &&
            planetarium.Name?.Contains("Stellarium", StringComparison.OrdinalIgnoreCase) == true)
        {
            var identity = await TryReadStellariumIdentityAsync(
                targetCoordinates,
                target?.Name,
                cancellationToken).ConfigureAwait(false);
            if (identity is not null)
            {
                importedName = identity.TargetName;
                importedCatalogId = identity.CatalogId ?? target?.Id;
                identityNote = $"已用同一时刻的 Stellarium 选择详情按坐标复核，目标/文件名采用“{identity.TargetName}”"
                    + (string.IsNullOrWhiteSpace(importedCatalogId) ? "。" : $"；目录标识为 {importedCatalogId}。")
                    + (string.IsNullOrWhiteSpace(target?.Name) ? string.Empty : $" 星图本地化显示名为“{target.Name.Trim()}”。");
            }
        }

        double? positionAngle = target?.PositionAngle?.Degree;
        string? rotationNote = null;
        if (planetarium.CanGetRotationAngle)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                positionAngle = await planetarium.GetRotationAngle().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                rotationNote = $"星图位置角未导入：{ObservationTargetImportErrors.SafeMessage(ex)}。";
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Rotation is optional target metadata. Preserve the named target
                // snapshot and explain why PA could not be attached.
                rotationNote = $"星图位置角未导入：{ObservationTargetImportErrors.SafeMessage(ex)}。";
            }
        }

        var sourceName = string.IsNullOrWhiteSpace(planetarium.Name)
            ? "N.I.N.A. 第三方星图"
            : $"N.I.N.A. 第三方星图 / {planetarium.Name.Trim()}";
        var cancellationNote = "N.I.N.A. 3.2 的星图读取接口不接收取消令牌；本次单次读取仅在调用前后检查取消，不会建立持续跟随。";
        var sourceDetails = string.Join(
            " ",
            new[] { cancellationNote, identityNote, rotationNote }
                .Where(note => !string.IsNullOrWhiteSpace(note)));

        return new ObservationPlanetariumTargetSnapshot(
            importedName,
            importedCatalogId,
            targetCoordinates,
            positionAngle,
            sourceName,
            sourceDetails);
    }

    private async Task<StellariumSelectedIdentity?> TryReadStellariumIdentityAsync(
        ObservationTargetCoordinates expectedCoordinates,
        string? ninaDisplayName,
        CancellationToken cancellationToken)
    {
        try
        {
            var endpoint = stellariumEndpoint?.Invoke();
            if (endpoint is null) return null;
            var requestUri = new Uri(endpoint, "api/objects/info?format=json");
            using var response = await StellariumClient.GetAsync(requestUri, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            if (root.TryGetProperty("found", out var found) && found.ValueKind == JsonValueKind.False) return null;

            var canonicalName = ReadNonBlankString(root, "name");
            if (canonicalName is null) return null;
            if (!TryReadFiniteDouble(root, "raJ2000", out var rightAscensionDegrees) ||
                !TryReadFiniteDouble(root, "decJ2000", out var declinationDegrees))
            {
                return null;
            }
            rightAscensionDegrees = ((rightAscensionDegrees % 360d) + 360d) % 360d;
            var selectedCoordinates = new ObservationTargetCoordinates(rightAscensionDegrees, declinationDegrees);
            if (AngularSeparationArcSeconds(expectedCoordinates, selectedCoordinates) > 5d) return null;

            var resolved = ResolveStellariumIdentity(
                canonicalName,
                ReadNonBlankString(root, "localized-name"),
                ReadNonBlankString(root, "designation"),
                ninaDisplayName);
            return new StellariumSelectedIdentity(resolved.TargetName, resolved.CatalogId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // The N.I.N.A. IPlanetarium result remains authoritative for pointing.
            // Name/catalog enrichment is optional and must never make import fail.
            return null;
        }
    }

    /// <summary>
    /// Splits Stellarium's selected-object identity into the human target name used
    /// for OBJECT/file naming and a catalog identifier. Stellarium commonly returns
    /// a catalog identifier (for example "HIP 5447") in <c>name</c>, places the
    /// localized common name in <c>localized-name</c>, and omits <c>designation</c>.
    /// </summary>
    internal static (string TargetName, string? CatalogId) ResolveStellariumIdentity(
        string canonicalName,
        string? localizedName,
        string? designation,
        string? ninaDisplayName)
    {
        var canonical = canonicalName.Trim();
        var catalogId = string.IsNullOrWhiteSpace(designation)
            ? (LooksLikeCatalogIdentifier(canonical) ? canonical : null)
            : designation.Trim();

        if (!LooksLikeCatalogIdentifier(canonical) && ContainsAsciiLetter(canonical))
        {
            return (canonical, catalogId);
        }

        var localized = FirstNonBlank(localizedName, ninaDisplayName);
        return (localized ?? canonical, catalogId);
    }

    private static bool LooksLikeCatalogIdentifier(string value)
    {
        var trimmed = value.Trim();
        if (!trimmed.Any(char.IsDigit)) return false;

        string[] prefixes =
        [
            "HIP", "HD", "HR", "SAO", "TYC", "GAIA", "2MASS", "UCAC", "GSC", "TIC",
            "WDS", "BD", "CD", "CPD", "NGC", "IC", "UGC", "PGC", "MESSIER", "M",
        ];
        return prefixes.Any(prefix =>
            trimmed.StartsWith(prefix + " ", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase) ||
            (prefix is "M" && trimmed.Length > 1 && char.IsDigit(trimmed[1])) ||
            (prefix is "UCAC" && trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
    }

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static bool ContainsAsciiLetter(string value) =>
        value.Any(character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z');

    private static string? ReadNonBlankString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;

    private static bool TryReadFiniteDouble(JsonElement root, string propertyName, out double value)
    {
        value = 0;
        return root.TryGetProperty(propertyName, out var element) &&
            element.TryGetDouble(out value) &&
            double.IsFinite(value);
    }

    private static double AngularSeparationArcSeconds(
        ObservationTargetCoordinates left,
        ObservationTargetCoordinates right)
    {
        const double degreesToRadians = Math.PI / 180d;
        var leftRa = left.RightAscensionDegrees * degreesToRadians;
        var rightRa = right.RightAscensionDegrees * degreesToRadians;
        var leftDec = left.DeclinationDegrees * degreesToRadians;
        var rightDec = right.DeclinationDegrees * degreesToRadians;
        var cosine = Math.Sin(leftDec) * Math.Sin(rightDec) +
            Math.Cos(leftDec) * Math.Cos(rightDec) * Math.Cos(leftRa - rightRa);
        return Math.Acos(Math.Clamp(cosine, -1d, 1d)) / degreesToRadians * 3600d;
    }

    private sealed record StellariumSelectedIdentity(string TargetName, string? CatalogId);

    private static ObservationTargetCoordinates ToJ2000(Coordinates coordinates, string sourceName)
    {
        try
        {
            var j2000 = coordinates.Epoch == Epoch.J2000
                ? coordinates
                : coordinates.Transform(Epoch.J2000);
            return new ObservationTargetCoordinates(j2000.RADegrees, j2000.Dec);
        }
        catch (Exception ex)
        {
            throw new ObservationTargetImportException(
                "TARGET_EPOCH_CONVERSION_FAILED",
                $"{sourceName} 返回的目标无法转换为 J2000 坐标：{ObservationTargetImportErrors.SafeMessage(ex)}",
                ex);
        }
    }
}
