using NINA.Astrometry;
using NINA.Equipment.Interfaces;
using NINA.WPF.Base.Interfaces.ViewModel;
using System.Net.Http;
using System.Text.Json;
using UvexAdv.Observatory;

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
        MaxResponseContentBufferSize = 2 * 1024 * 1024,
    };

    private readonly IPlanetariumFactory planetariumFactory;
    private readonly Func<Uri?>? stellariumEndpoint;
    private readonly HttpClient stellariumHttp;
    private readonly Func<double, StellariumCoordinateNormalizer.Velocity>? velocityProvider;

    public NinaPlanetariumTargetSource(IPlanetariumFactory planetariumFactory)
        : this(planetariumFactory, null)
    {
    }

    internal NinaPlanetariumTargetSource(
        IPlanetariumFactory planetariumFactory,
        Func<Uri?>? stellariumEndpoint,
        HttpClient? stellariumHttp = null,
        Func<double, StellariumCoordinateNormalizer.Velocity>? velocityProvider = null)
    {
        this.planetariumFactory = planetariumFactory ?? throw new ArgumentNullException(nameof(planetariumFactory));
        this.stellariumEndpoint = stellariumEndpoint;
        this.stellariumHttp = stellariumHttp ?? StellariumClient;
        this.velocityProvider = velocityProvider;
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
        TargetCatalogMetadata? catalogMetadata = null;
        string? identityNote = null;
        if (targetCoordinates is not null &&
            planetarium.Name?.Contains("Stellarium", StringComparison.OrdinalIgnoreCase) == true)
        {
            var identity = await ReadStellariumIdentityAsync(
                targetCoordinates,
                target?.Name,
                cancellationToken).ConfigureAwait(false);
            if (identity is not null)
            {
                importedName = identity.TargetName;
                importedCatalogId = identity.CatalogId ?? target?.Id;
                catalogMetadata = identity.CatalogMetadata with { CatalogId = importedCatalogId };
                targetCoordinates = new(catalogMetadata.RightAscensionDegrees, catalogMetadata.DeclinationDegrees);
                identityNote = $"已用同一时刻的 Stellarium 选择详情按坐标复核，目标/文件名采用“{identity.TargetName}”"
                    + (string.IsNullOrWhiteSpace(importedCatalogId) ? "。" : $"；目录标识为 {importedCatalogId}。")
                    + (string.IsNullOrWhiteSpace(target?.Name) ? string.Empty : $" 星图本地化显示名为“{target.Name.Trim()}”。")
                    + $" 已按接口去除光行差 {catalogMetadata.CoordinateProvenance!.CorrectionArcseconds:F3}″，保存标准 J2000 轴向天体测量坐标；赤道仪所需当前坐标由 N.I.N.A. 单独转换，不重复换算。";
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
        if (catalogMetadata is null && targetCoordinates is not null && !string.IsNullOrWhiteSpace(importedName))
        {
            // Other planetaria may supply native DSOType/Magnitude. Missing fields
            // remain unknown; do not infer an object type from its common name.
            catalogMetadata = new TargetCatalogMetadata(importedName, importedCatalogId,
                targetCoordinates.RightAscensionDegrees, targetCoordinates.DeclinationDegrees,
                sourceName, target?.DSOType, NormalizeCatalogMagnitude(target?.Magnitude), DateTimeOffset.UtcNow);
        }
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
            sourceDetails,
            catalogMetadata);
    }

    private async Task<StellariumSelectedIdentity?> ReadStellariumIdentityAsync(
        ObservationTargetCoordinates expectedCoordinates,
        string? ninaDisplayName,
        CancellationToken cancellationToken)
    {
        try
        {
            var endpoint = stellariumEndpoint?.Invoke();
            if (endpoint is null) throw new InvalidOperationException("The configured Stellarium RemoteControl endpoint is required for coordinate normalization.");
            async Task<JsonDocument> Read(string relative)
            {
                var bytes = await stellariumHttp.GetByteArrayAsync(new Uri(endpoint, relative), cancellationToken).ConfigureAwait(false);
                if (bytes.Length > 2 * 1024 * 1024) throw new InvalidOperationException("Stellarium response is too large.");
                return JsonDocument.Parse(bytes);
            }
            using var statusBefore = await Read("api/main/status").ConfigureAwait(false);
            using var propertiesBefore = await Read("api/stelproperty/list").ConfigureAwait(false);
            var before = StellariumCoordinateNormalizer.ReadState(statusBefore.RootElement, propertiesBefore.RootElement);
            using var document = await Read("api/objects/info?format=json").ConfigureAwait(false);
            var root = document.RootElement;
            if (root.TryGetProperty("found", out var found) && found.ValueKind == JsonValueKind.False)
                throw new InvalidOperationException("No selected catalogue object.");

            var canonicalName = ReadNonBlankString(root, "name");
            if (canonicalName is null) throw new InvalidOperationException("Selected object has no stable name.");
            if (!TryReadFiniteDouble(root, "raJ2000", out var rightAscensionDegrees) ||
                !TryReadFiniteDouble(root, "decJ2000", out var declinationDegrees))
            {
                throw new InvalidOperationException("Stellarium did not supply raJ2000/decJ2000.");
            }
            rightAscensionDegrees = ((rightAscensionDegrees % 360d) + 360d) % 360d;
            var selectedCoordinates = new ObservationTargetCoordinates(rightAscensionDegrees, declinationDegrees);
            if (AngularSeparationArcSeconds(expectedCoordinates, selectedCoordinates) > 5d)
                throw new InvalidOperationException("The NINA result and selected object differ (selection changed or CCD framing center). Import the selected object, not a view center with its name.");

            using var propertiesAfter = await Read("api/stelproperty/list").ConfigureAwait(false);
            using var statusAfter = await Read("api/main/status").ConfigureAwait(false);
            var after = StellariumCoordinateNormalizer.ReadState(statusAfter.RootElement, propertiesAfter.RootElement);
            if (!StellariumCoordinateNormalizer.SameState(before, after))
                throw new InvalidOperationException("Stellarium time, observer or astrometry settings changed during import.");
            using var selectionAfter = await Read("api/objects/info?format=json").ConfigureAwait(false);
            var last = selectionAfter.RootElement;
            if (ReadNonBlankString(last, "name") != canonicalName ||
                ReadNonBlankString(last, "type") != ReadNonBlankString(root, "type") ||
                !TryReadFiniteDouble(last, "raJ2000", out var lastRa) ||
                !TryReadFiniteDouble(last, "decJ2000", out var lastDec) ||
                AngularSeparationArcSeconds(selectedCoordinates, new(lastRa, lastDec)) > 0.1)
                throw new InvalidOperationException("Stellarium selection changed while its coordinate context was being read.");
            var normalized = StellariumCoordinateNormalizer.Normalize(selectedCoordinates,
                before with { JulianDay = (before.JulianDay + after.JulianDay) / 2 },
                ReadNonBlankString(root, "type") ?? string.Empty, ReadNonBlankString(root, "object-type"), velocityProvider);

            var resolved = ResolveStellariumIdentity(
                canonicalName,
                ReadNonBlankString(root, "localized-name"),
                ReadNonBlankString(root, "designation"),
                ninaDisplayName);
            var metadata = ParseStellariumCatalogMetadata(root, resolved.TargetName, resolved.CatalogId,
                expectedCoordinates, DateTimeOffset.UtcNow);
            if (metadata is null) throw new InvalidOperationException("Selected-object metadata did not match its coordinates.");
            metadata = metadata with
            {
                RightAscensionDegrees = normalized.Coordinates.RightAscensionDegrees,
                DeclinationDegrees = normalized.Coordinates.DeclinationDegrees,
                CoordinateProvenance = normalized.Provenance,
            };
            return new StellariumSelectedIdentity(resolved.TargetName, resolved.CatalogId, metadata);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Unknown apparent-place semantics must not be relabelled as a
            // catalogue position. Existing/manual draft remains untouched.
            throw new ObservationTargetImportException("STELLARIUM_COORDINATE_CONVERSION_FAILED",
                "Stellarium 坐标未完成标准化；未覆盖目标草稿。可手工输入可信目录的标准 J2000 坐标。 " +
                ObservationTargetImportErrors.SafeMessage(ex), ex);
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
            element.ValueKind == JsonValueKind.Number &&
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

    /// <summary>
    /// Stellarium getInfoMap exposes type, object-type, star-type and vmag.
    /// Nebula::getInfoMap localizes type, so object-type is retained independently.
    /// This is the same selected-object read used for name identity enrichment;
    /// metadata from a changed selection cannot attach to the previous target.
    /// </summary>
    internal static TargetCatalogMetadata? ParseStellariumCatalogMetadata(
        JsonElement root, string targetName, string? catalogId,
        ObservationTargetCoordinates expectedCoordinates, DateTimeOffset capturedUtc)
    {
        if (!double.IsFinite(expectedCoordinates.RightAscensionDegrees) || expectedCoordinates.RightAscensionDegrees is < 0 or >= 360 ||
            !double.IsFinite(expectedCoordinates.DeclinationDegrees) || expectedCoordinates.DeclinationDegrees is < -90 or > 90 ||
            root.ValueKind != JsonValueKind.Object ||
            (root.TryGetProperty("found", out var found) && found.ValueKind == JsonValueKind.False) ||
            !TryReadFiniteDouble(root, "raJ2000", out var ra) ||
            !TryReadFiniteDouble(root, "decJ2000", out var dec) || dec is < -90 or > 90)
            return null;
        ra = ((ra % 360d) + 360d) % 360d;
        if (AngularSeparationArcSeconds(expectedCoordinates, new ObservationTargetCoordinates(ra, dec)) > 5d)
            return null;
        var magnitude = TryReadFiniteDouble(root, "vmag", out var vmag) ? NormalizeCatalogMagnitude(vmag) : null;
        return new TargetCatalogMetadata(targetName, catalogId,
            expectedCoordinates.RightAscensionDegrees, expectedCoordinates.DeclinationDegrees,
            "Stellarium /api/objects/info (同次选择、坐标复核)",
            ReadNonBlankString(root, "type"), magnitude, capturedUtc,
            ReadNonBlankString(root, "object-type") ?? ReadNonBlankString(root, "star-type"));
    }

    private static double? NormalizeCatalogMagnitude(double? magnitude) =>
        magnitude is { } value && double.IsFinite(value) && value is > -40 and < 50 ? value : null;

    private sealed record StellariumSelectedIdentity(string TargetName, string? CatalogId, TargetCatalogMetadata CatalogMetadata);

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
