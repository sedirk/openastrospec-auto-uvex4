using NINA.Astrometry;
using NINA.Equipment.Interfaces;
using NINA.WPF.Base.Interfaces.ViewModel;

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
        Func<DateTimeOffset>? utcNow = null) =>
        new(
            new NinaFramingAssistantTargetSource(framingAssistant),
            new NinaPlanetariumTargetSource(planetariumFactory),
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
    private readonly IPlanetariumFactory planetariumFactory;

    public NinaPlanetariumTargetSource(IPlanetariumFactory planetariumFactory)
    {
        this.planetariumFactory = planetariumFactory ?? throw new ArgumentNullException(nameof(planetariumFactory));
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
        var sourceDetails = rotationNote is null
            ? cancellationNote
            : $"{cancellationNote} {rotationNote}";

        return new ObservationPlanetariumTargetSnapshot(
            target?.Name,
            target?.Id,
            targetCoordinates,
            positionAngle,
            sourceName,
            sourceDetails);
    }

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
