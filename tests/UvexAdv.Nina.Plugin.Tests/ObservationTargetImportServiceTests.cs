using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class ObservationTargetImportServiceTests
{
    private static readonly DateTimeOffset ImportedUtc =
        new(2026, 8, 18, 11, 22, 33, TimeSpan.Zero);

    [Fact]
    public void FramingImportDefaultsToInitialRectangleCenterAndCarriesMovedCenter()
    {
        var framing = new FakeFramingSource(new ObservationFramingTargetSnapshot(
            true,
            "Nova Tauri 2026",
            "TCP J05210763+2338194",
            "Nova Tauri 2026",
            new ObservationTargetCoordinates(80.5, 23.7),
            new ObservationTargetCoordinates(80.27918002, 23.63868315),
            new ObservationTargetCoordinates(80.5, 23.7),
            1,
            1,
            1,
            true,
            371.5,
            "N.I.N.A. 构图助手"));
        var service = CreateService(framing);

        var result = service.ImportFromFramingAssistant();

        Assert.Equal(1, framing.CaptureCount);
        Assert.False(result.UsedFramingCenter);
        Assert.Equal(80.27918002, result.RightAscensionDegrees, 8);
        Assert.Equal(23.63868315, result.DeclinationDegrees, 8);
        Assert.Equal(80.5, Assert.IsType<ObservationTargetCoordinates>(result.FramingCenterCoordinates).RightAscensionDegrees, 8);
        Assert.Equal("J2000", result.Epoch);
        Assert.Equal(string.Empty, result.CatalogId);
        Assert.Equal(11.5, result.PositionAngleDegrees);
        Assert.Contains("本次构图矩形初始中心 J2000", result.Details, StringComparison.Ordinal);
        Assert.Contains("当前构图中心 J2000", result.Details, StringComparison.Ordinal);
        Assert.Contains("默认导入采用本次矩形的初始中心", result.Details, StringComparison.Ordinal);
    }

    [Fact]
    public void FramingCenterIsUsedOnlyWhenExplicitlyRequested()
    {
        var framing = new FakeFramingSource(ValidFramingSnapshot() with
        {
            DeepSkyObjectCoordinates = new ObservationTargetCoordinates(11, 21),
            InitialFramingCoordinates = new ObservationTargetCoordinates(10, 20),
            FramingCenterCoordinates = new ObservationTargetCoordinates(11, 21),
        });
        var service = CreateService(framing);

        var result = service.ImportFromFramingAssistant(useFramingCenter: true);

        Assert.True(result.UsedFramingCenter);
        Assert.Equal(11, result.RightAscensionDegrees);
        Assert.Equal(21, result.DeclinationDegrees);
        Assert.EndsWith("/ 当前构图中心", result.Source, StringComparison.Ordinal);
        Assert.Contains("本次显式采用当前构图中心", result.Details, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(2, 1, 2)]
    [InlineData(1, 2, 2)]
    [InlineData(1, 1, 2)]
    public void MultiPanelFramingIsExplicitlyRejected(int horizontal, int vertical, int rectangleCount)
    {
        var framing = new FakeFramingSource(ValidFramingSnapshot() with
        {
            HorizontalPanels = horizontal,
            VerticalPanels = vertical,
            FramingRectangleCount = rectangleCount,
        });
        var service = CreateService(framing);

        var error = Assert.Throws<ObservationTargetImportException>(
            () => service.ImportFromFramingAssistant());

        Assert.Equal("FRAMING_MULTI_PANEL_UNSUPPORTED", error.Code);
        Assert.Contains("不会静默取第一个面板", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingFramingDsoIsRejectedEvenWhenCenterExists()
    {
        var framing = new FakeFramingSource(ValidFramingSnapshot() with
        {
            HasDeepSkyObject = false,
            TargetName = null,
            InitialFramingCoordinates = null,
            FramingCenterCoordinates = new ObservationTargetCoordinates(30, 40),
        });
        var service = CreateService(framing);

        var error = Assert.Throws<ObservationTargetImportException>(
            () => service.ImportFromFramingAssistant(useFramingCenter: true));

        Assert.Equal("FRAMING_NO_DSO", error.Code);
        Assert.Contains("不能静默冒充", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnnamedFramingDsoIsRejected()
    {
        var framing = new FakeFramingSource(ValidFramingSnapshot() with { TargetName = "  " });
        var service = CreateService(framing);

        var error = Assert.Throws<ObservationTargetImportException>(
            () => service.ImportFromFramingAssistant());

        Assert.Equal("FRAMING_UNNAMED_DSO", error.Code);
        Assert.Contains("命名天体", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RequestedFramingCenterMustExist()
    {
        var framing = new FakeFramingSource(ValidFramingSnapshot() with
        {
            FramingCenterCoordinates = null,
        });
        var service = CreateService(framing);

        var error = Assert.Throws<ObservationTargetImportException>(
            () => service.ImportFromFramingAssistant(useFramingCenter: true));

        Assert.Equal("FRAMING_CENTER_UNAVAILABLE", error.Code);
        Assert.Contains("改用默认的本次构图矩形初始中心", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RequestedFramingCenterMustBeCurrent()
    {
        var framing = new FakeFramingSource(ValidFramingSnapshot() with
        {
            FramingCenterCalculated = false,
        });
        var service = CreateService(framing);

        var error = Assert.Throws<ObservationTargetImportException>(
            () => service.ImportFromFramingAssistant(useFramingCenter: true));

        Assert.Equal("FRAMING_CENTER_NOT_CURRENT", error.Code);
        Assert.Contains("避免导入陈旧坐标", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NewDsoNameCannotBeCombinedWithPreviousRectangleName()
    {
        var framing = new FakeFramingSource(ValidFramingSnapshot() with
        {
            TargetName = "New target",
            FramingRectangleTargetName = "Previous target",
            InitialFramingCoordinates = new ObservationTargetCoordinates(10, 20),
            FramingCenterCoordinates = new ObservationTargetCoordinates(10, 20),
            FramingCenterCalculated = true,
        });
        var service = CreateService(framing);

        var error = Assert.Throws<ObservationTargetImportException>(
            () => service.ImportFromFramingAssistant());

        Assert.Equal("FRAMING_TARGET_RECTANGLE_STALE", error.Code);
        Assert.Contains("不是同一代快照", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NewDsoCoordinatesCannotBeCombinedWithPreviousRectangleCoordinates()
    {
        var framing = new FakeFramingSource(ValidFramingSnapshot() with
        {
            DeepSkyObjectCoordinates = new ObservationTargetCoordinates(80, 20),
            InitialFramingCoordinates = new ObservationTargetCoordinates(10, 20),
            FramingCenterCoordinates = new ObservationTargetCoordinates(10, 20),
            FramingCenterCalculated = true,
        });
        var service = CreateService(framing);

        var error = Assert.Throws<ObservationTargetImportException>(
            () => service.ImportFromFramingAssistant());

        Assert.Equal("FRAMING_TARGET_RECTANGLE_STALE", error.Code);
        Assert.Contains("不是同一代快照", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlanetariumImportIsOneDetachedNamedTargetSnapshot()
    {
        var planetarium = new FakePlanetariumSource(new ObservationPlanetariumTargetSnapshot(
            "新星 金牛座 2026",
            "新星 金牛座 2026",
            new ObservationTargetCoordinates(80.27918002, 23.63868315),
            -5,
            "N.I.N.A. 第三方星图 / Stellarium",
            "单次读取，不持续跟随。"));
        var service = CreateService(planetarium: planetarium);

        var result = await service.ImportFromPlanetariumAsync();

        Assert.Equal(1, planetarium.CaptureCount);
        Assert.Equal("新星 金牛座 2026", result.TargetName);
        Assert.Equal(string.Empty, result.CatalogId);
        Assert.Equal(80.27918002, result.RightAscensionDegrees, 8);
        Assert.Equal(23.63868315, result.DeclinationDegrees, 8);
        Assert.Equal(355, result.PositionAngleDegrees);
        Assert.Equal(ImportedUtc, result.ImportedUtc);
        Assert.Equal("J2000", result.Epoch);
        Assert.Null(result.FramingCenterCoordinates);
        Assert.False(result.UsedFramingCenter);
        Assert.Contains("目标名称保留星图显示名“新星 金牛座 2026”", result.Details, StringComparison.Ordinal);
        Assert.Contains("不会再由目录号或坐标名称覆盖", result.Details, StringComparison.Ordinal);
        Assert.Contains("不会连接设备、移动赤道仪或启动观测", result.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlanetariumEnglishNameAndAsciiDesignationArePreservedForOutput()
    {
        var planetarium = new FakePlanetariumSource(new ObservationPlanetariumTargetSnapshot(
            "Nova Sagitta 2026",
            "Gaia DR3 1824166210904571136",
            new ObservationTargetCoordinates(296.28155945, 18.38125749),
            null,
            "N.I.N.A. 第三方星图 / Stellarium"));
        var service = CreateService(planetarium: planetarium);

        var result = await service.ImportFromPlanetariumAsync();

        Assert.Equal("Nova Sagitta 2026", result.TargetName);
        Assert.Equal("Gaia DR3 1824166210904571136", result.CatalogId);
        Assert.Contains("目标名称保留星图显示名“Nova Sagitta 2026”", result.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlanetariumBilingualNameIsNotOverwrittenByItsHipCatalogId()
    {
        var planetarium = new FakePlanetariumSource(new ObservationPlanetariumTargetSnapshot(
            "奎宿九 Mirach",
            "HIP 5447",
            new ObservationTargetCoordinates(17.43923540, 35.61923457),
            null,
            "N.I.N.A. 第三方星图 / Stellarium"));
        var service = CreateService(planetarium: planetarium);

        var result = await service.ImportFromPlanetariumAsync();

        Assert.Equal("奎宿九 Mirach", result.TargetName);
        Assert.Equal("HIP 5447", result.CatalogId);
        Assert.Contains("目标名称保留星图显示名“奎宿九 Mirach”", result.Details, StringComparison.Ordinal);
        Assert.Contains("独立目录标识为 HIP 5447", result.Details, StringComparison.Ordinal);
    }

    [Fact]
    public void StellariumCatalogNameAndLocalizedCommonNameAreSeparated()
    {
        var identity = NinaPlanetariumTargetSource.ResolveStellariumIdentity(
            "HIP 5447",
            "奎宿九 Mirach",
            designation: null,
            ninaDisplayName: "奎宿九 Mirach");

        Assert.Equal("奎宿九 Mirach", identity.TargetName);
        Assert.Equal("HIP 5447", identity.CatalogId);
    }

    [Fact]
    public void StellariumCanonicalCommonNameKeepsExplicitDesignation()
    {
        var identity = NinaPlanetariumTargetSource.ResolveStellariumIdentity(
            "Nova Sagitta 2026",
            "新星 天箭座 2026",
            "Gaia DR3 1824166210904571136",
            ninaDisplayName: "新星 天箭座 2026");

        Assert.Equal("Nova Sagitta 2026", identity.TargetName);
        Assert.Equal("Gaia DR3 1824166210904571136", identity.CatalogId);
    }

    [Fact]
    public void StellariumCatalogWithoutCommonAliasStillPopulatesBothFields()
    {
        var identity = NinaPlanetariumTargetSource.ResolveStellariumIdentity(
            "HIP 5447",
            "HIP 5447",
            designation: null,
            ninaDisplayName: "HIP 5447");

        Assert.Equal("HIP 5447", identity.TargetName);
        Assert.Equal("HIP 5447", identity.CatalogId);
    }

    [Fact]
    public async Task PlanetariumRequiresAnExplicitlyNamedTarget()
    {
        var planetarium = new FakePlanetariumSource(new ObservationPlanetariumTargetSnapshot(
            " ",
            "",
            new ObservationTargetCoordinates(10, 20),
            null,
            "Stellarium"));
        var service = CreateService(planetarium: planetarium);

        var error = await Assert.ThrowsAsync<ObservationTargetImportException>(
            () => service.ImportFromPlanetariumAsync());

        Assert.Equal("PLANETARIUM_NO_NAMED_TARGET", error.Code);
        Assert.Contains("先在 Stellarium 中点击目标", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Planetarium404IsExplainedAsNoSelectedObject()
    {
        var planetarium = new ThrowingPlanetariumSource(
            new HttpRequestException("Response status code does not indicate success: 404 (Not Found).", null, HttpStatusCode.NotFound));
        var service = CreateService(planetarium: planetarium);

        var error = await Assert.ThrowsAsync<ObservationTargetImportException>(
            () => service.ImportFromPlanetariumAsync());

        Assert.Equal("PLANETARIUM_NO_SELECTED_OBJECT", error.Code);
        Assert.Contains("没有明确选中的命名目标", error.Message, StringComparison.Ordinal);
        Assert.Contains("点击目标", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RightAscensionOutsideOneTurnIsRejected()
    {
        var planetarium = new FakePlanetariumSource(new ObservationPlanetariumTargetSnapshot(
            "Named target",
            "CAT 1",
            new ObservationTargetCoordinates(725.25, -30),
            null,
            "Test planetarium"));
        var service = CreateService(planetarium: planetarium);

        var error = await Assert.ThrowsAsync<ObservationTargetImportException>(
            () => service.ImportFromPlanetariumAsync());

        Assert.Equal("TARGET_COORDINATES_INVALID", error.Code);
        Assert.Contains("0°（含）至 360°（不含）", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RawHttpFailureIsExplainedAsPlanetariumConnectionFailure()
    {
        var planetarium = new ThrowingPlanetariumSource(
            new HttpRequestException("No connection could be made because the target machine actively refused it."));
        var service = CreateService(planetarium: planetarium);

        var error = await Assert.ThrowsAsync<ObservationTargetImportException>(
            () => service.ImportFromPlanetariumAsync());

        Assert.Equal("PLANETARIUM_CONNECTION_FAILED", error.Code);
        Assert.Contains("Stellarium", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TransportTimeoutIsExplainedAsPlanetariumConnectionFailure()
    {
        var planetarium = new ThrowingPlanetariumSource(new TaskCanceledException("HTTP request timed out."));
        var service = CreateService(planetarium: planetarium);

        var error = await Assert.ThrowsAsync<ObservationTargetImportException>(
            () => service.ImportFromPlanetariumAsync());

        Assert.Equal("PLANETARIUM_CONNECTION_FAILED", error.Code);
        Assert.Contains("超时", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidDeclinationIsRejected()
    {
        var planetarium = new FakePlanetariumSource(new ObservationPlanetariumTargetSnapshot(
            "Named target",
            "CAT 1",
            new ObservationTargetCoordinates(10, 90.01),
            null,
            "Test planetarium"));
        var service = CreateService(planetarium: planetarium);

        var error = await Assert.ThrowsAsync<ObservationTargetImportException>(
            () => service.ImportFromPlanetariumAsync());

        Assert.Equal("TARGET_COORDINATES_INVALID", error.Code);
        Assert.Contains("−90° 至 +90°", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NinaFramingAdapterUsesOriginalCoordinatesAndRaDegrees()
    {
        var astrometry = LoadNinaAssembly("NINA.Astrometry");
        var core = LoadNinaAssembly("NINA.Core");
        var wpfBase = LoadNinaAssembly("NINA.WPF.Base");
        var coordinatesType = RequiredType(astrometry, "NINA.Astrometry.Coordinates");
        var rectangleType = RequiredType(astrometry, "NINA.Astrometry.FramingRectangle");
        var dsoType = RequiredType(astrometry, "NINA.Astrometry.DeepSkyObject");
        var original = CreateCoordinates(coordinatesType, 5d, 20d, "JNOW");
        var overwrittenDsoCoordinates = CreateCoordinates(coordinatesType, 6d, 21d, "JNOW");
        var movedCenter = CreateCoordinates(coordinatesType, 5.1d, 20.1d, "JNOW");
        var rectangle = Activator.CreateInstance(rectangleType, 0d, 0d, 0d, 100d, 100d)!;
        SetProperty(rectangle, "OriginalCoordinates", original);
        SetProperty(rectangle, "Coordinates", movedCenter);
        SetProperty(rectangle, "TotalRotation", 12.5d);
        SetProperty(rectangle, "DSOPositionAngle", 18.5d);
        SetProperty(rectangle, "Name", "Target");
        var collectionType = RequiredType(core, "NINA.Core.Utility.AsyncObservableCollection`1")
            .MakeGenericType(rectangleType);
        var listType = typeof(List<>).MakeGenericType(rectangleType);
        var rectangleList = Activator.CreateInstance(listType)!;
        listType.GetMethod("Add")!.Invoke(rectangleList, [rectangle]);
        var rectangles = Activator.CreateInstance(collectionType, rectangleList)!;
        var dso = Activator.CreateInstance(dsoType, "Target", overwrittenDsoCoordinates, null)!;
        SetProperty(dso, "Name", "Target");
        var framingAssistantType = RequiredType(wpfBase, "NINA.WPF.Base.Interfaces.ViewModel.IFramingAssistantVM");
        var framingAssistant = DispatchProxy.Create(framingAssistantType, typeof(DynamicInterfaceProxy));
        var proxy = (DynamicInterfaceProxy)framingAssistant;
        proxy.Values["get_DSO"] = dso;
        proxy.Values["get_CameraRectangles"] = rectangles;
        proxy.Values["get_HorizontalPanels"] = 1;
        proxy.Values["get_VerticalPanels"] = 1;
        proxy.Values["get_Rectangle"] = rectangle;
        proxy.Values["get_RectangleCalculated"] = true;
        var source = Activator.CreateInstance(typeof(NinaFramingAssistantTargetSource), framingAssistant)!;

        var snapshot = (ObservationFramingTargetSnapshot)source.GetType().GetMethod("Capture")!.Invoke(source, null)!;

        var expectedOriginalJ2000 = TransformToJ2000(coordinatesType, original);
        var expectedCenterJ2000 = TransformToJ2000(coordinatesType, movedCenter);
        var overwrittenJ2000 = TransformToJ2000(coordinatesType, overwrittenDsoCoordinates);
        Assert.Equal(GetDouble(expectedOriginalJ2000, "RADegrees"), snapshot.InitialFramingCoordinates!.RightAscensionDegrees, 8);
        Assert.Equal(GetDouble(expectedOriginalJ2000, "Dec"), snapshot.InitialFramingCoordinates.DeclinationDegrees, 8);
        Assert.Equal(GetDouble(expectedCenterJ2000, "RADegrees"), snapshot.FramingCenterCoordinates!.RightAscensionDegrees, 8);
        Assert.NotEqual(GetDouble(overwrittenJ2000, "RADegrees"), snapshot.InitialFramingCoordinates.RightAscensionDegrees);
        Assert.NotEqual(GetDouble(expectedOriginalJ2000, "RA"), snapshot.InitialFramingCoordinates.RightAscensionDegrees);
        Assert.True(snapshot.FramingCenterCalculated);
        Assert.Equal("Target", snapshot.FramingRectangleTargetName);
        Assert.Equal(18.5, snapshot.PositionAngleDegrees);
    }

    [Fact]
    public async Task NinaPlanetariumAdapterReportsMissingConfiguration()
    {
        var source = CreateNinaPlanetariumSource(planetarium: null);

        var error = await Assert.ThrowsAsync<ObservationTargetImportException>(
            () => InvokePlanetariumCaptureAsync(source));

        Assert.Equal("PLANETARIUM_NOT_CONFIGURED", error.Code);
        Assert.Contains("尚未配置第三方星图", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NinaPlanetariumAdapterTransformsJnowAndUsesRaDegrees()
    {
        var astrometry = LoadNinaAssembly("NINA.Astrometry");
        var coordinatesType = RequiredType(astrometry, "NINA.Astrometry.Coordinates");
        var dsoType = RequiredType(astrometry, "NINA.Astrometry.DeepSkyObject");
        var coordinates = CreateCoordinates(coordinatesType, 5d, 23.5d, "JNOW");
        var target = Activator.CreateInstance(dsoType, "Selected target", coordinates, null)!;
        SetProperty(target, "Name", "Selected target");
        var planetarium = CreatePlanetariumProxy(dsoType, TaskFromResult(dsoType, target));
        var source = CreateNinaPlanetariumSource(planetarium);

        var snapshot = await InvokePlanetariumCaptureAsync(source);

        var expected = TransformToJ2000(coordinatesType, coordinates);
        Assert.Equal(GetDouble(expected, "RADegrees"), snapshot.TargetBodyCoordinates!.RightAscensionDegrees, 8);
        Assert.Equal(GetDouble(expected, "Dec"), snapshot.TargetBodyCoordinates.DeclinationDegrees, 8);
        Assert.NotEqual(GetDouble(expected, "RA"), snapshot.TargetBodyCoordinates.RightAscensionDegrees);
    }

    [Fact]
    public async Task NinaPlanetariumAdapterTurnsRaw404IntoNoSelectionError()
    {
        var astrometry = LoadNinaAssembly("NINA.Astrometry");
        var dsoType = RequiredType(astrometry, "NINA.Astrometry.DeepSkyObject");
        var failure = new HttpRequestException("404 Not Found", null, HttpStatusCode.NotFound);
        var planetarium = CreatePlanetariumProxy(dsoType, TaskFromException(dsoType, failure));
        var source = CreateNinaPlanetariumSource(planetarium);

        var error = await Assert.ThrowsAsync<ObservationTargetImportException>(
            () => InvokePlanetariumCaptureAsync(source));

        Assert.Equal("PLANETARIUM_NO_SELECTED_OBJECT", error.Code);
        Assert.Contains("先在 Stellarium 中点击目标", error.Message, StringComparison.Ordinal);
    }

    private static ObservationTargetImportService CreateService(
        IObservationFramingTargetSource? framing = null,
        IObservationPlanetariumTargetSource? planetarium = null) =>
        new(
            framing ?? new FakeFramingSource(ValidFramingSnapshot()),
            planetarium ?? new FakePlanetariumSource(new ObservationPlanetariumTargetSnapshot(
                "Target",
                "CAT 1",
                new ObservationTargetCoordinates(1, 2),
                null,
                "Test planetarium")),
            () => ImportedUtc);

    private static ObservationFramingTargetSnapshot ValidFramingSnapshot() =>
        new(
            true,
            "Target",
            "CAT 1",
            "Target",
            new ObservationTargetCoordinates(10, 20),
            new ObservationTargetCoordinates(10, 20),
            new ObservationTargetCoordinates(10, 20),
            1,
            1,
            1,
            true,
            0,
            "N.I.N.A. 构图助手");

    private sealed class FakeFramingSource(ObservationFramingTargetSnapshot snapshot)
        : IObservationFramingTargetSource
    {
        public int CaptureCount { get; private set; }

        public ObservationFramingTargetSnapshot Capture()
        {
            CaptureCount++;
            return snapshot;
        }
    }

    private sealed class FakePlanetariumSource(ObservationPlanetariumTargetSnapshot snapshot)
        : IObservationPlanetariumTargetSource
    {
        public int CaptureCount { get; private set; }

        public Task<ObservationPlanetariumTargetSnapshot> CaptureAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CaptureCount++;
            return Task.FromResult(snapshot);
        }
    }

    private sealed class ThrowingPlanetariumSource(Exception exception)
        : IObservationPlanetariumTargetSource
    {
        public Task<ObservationPlanetariumTargetSnapshot> CaptureAsync(CancellationToken cancellationToken) =>
            Task.FromException<ObservationPlanetariumTargetSnapshot>(exception);
    }

    public class DynamicInterfaceProxy : DispatchProxy
    {
        public Dictionary<string, object?> Values { get; } = new(StringComparer.Ordinal);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is not null && Values.TryGetValue(targetMethod.Name, out var value))
            {
                return value;
            }

            var returnType = targetMethod?.ReturnType;
            return returnType is not null && returnType.IsValueType
                ? Activator.CreateInstance(returnType)
                : null;
        }
    }

    private static object CreateNinaPlanetariumSource(object? planetarium)
    {
        var equipment = LoadNinaAssembly("NINA.Equipment");
        var factoryType = RequiredType(equipment, "NINA.Equipment.Interfaces.IPlanetariumFactory");
        var factory = DispatchProxy.Create(factoryType, typeof(DynamicInterfaceProxy));
        ((DynamicInterfaceProxy)factory).Values["GetPlanetarium"] = planetarium;
        return Activator.CreateInstance(typeof(NinaPlanetariumTargetSource), factory)!;
    }

    private static object CreatePlanetariumProxy(Type dsoType, object getTargetTask)
    {
        _ = dsoType;
        var equipment = LoadNinaAssembly("NINA.Equipment");
        var planetariumType = RequiredType(equipment, "NINA.Equipment.Interfaces.IPlanetarium");
        var planetarium = DispatchProxy.Create(planetariumType, typeof(DynamicInterfaceProxy));
        var proxy = (DynamicInterfaceProxy)planetarium;
        proxy.Values["get_Name"] = "Stellarium";
        proxy.Values["get_CanGetRotationAngle"] = false;
        proxy.Values["GetTarget"] = getTargetTask;
        return planetarium;
    }

    private static async Task<ObservationPlanetariumTargetSnapshot> InvokePlanetariumCaptureAsync(object source)
    {
        var task = (Task<ObservationPlanetariumTargetSnapshot>)source.GetType()
            .GetMethod("CaptureAsync")!
            .Invoke(source, [CancellationToken.None])!;
        return await task;
    }

    private static Assembly LoadNinaAssembly(string simpleName)
    {
        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(NinaDirectory, $"{simpleName}.dll"));
        if (string.Equals(simpleName, "NINA.Astrometry", StringComparison.Ordinal))
        {
            try
            {
                NativeLibrary.SetDllImportResolver(assembly, (libraryName, _, _) =>
                {
                    if (string.Equals(libraryName, "NOVAS31lib.dll", StringComparison.OrdinalIgnoreCase))
                    {
                        return NativeLibrary.Load(Path.Combine(NinaDirectory, "External", "x64", "NOVAS", "NOVAS31lib.dll"));
                    }

                    if (string.Equals(libraryName, "SOFAlib.dll", StringComparison.OrdinalIgnoreCase))
                    {
                        return NativeLibrary.Load(Path.Combine(NinaDirectory, "External", "x64", "SOFA", "SOFAlib.dll"));
                    }

                    return IntPtr.Zero;
                });
            }
            catch (InvalidOperationException)
            {
                // Another adapter test already registered the resolver for this assembly.
            }
        }

        return assembly;
    }

    private static Type RequiredType(Assembly assembly, string fullName) =>
        assembly.GetType(fullName, throwOnError: true)!;

    private static object CreateCoordinates(Type coordinatesType, double raHours, double decDegrees, string epochName)
    {
        var epochType = coordinatesType.Assembly.GetType("NINA.Astrometry.Epoch", throwOnError: true)!;
        var raType = coordinatesType.GetNestedType("RAType", BindingFlags.Public)!;
        return Activator.CreateInstance(
            coordinatesType,
            raHours,
            decDegrees,
            Enum.Parse(epochType, epochName),
            Enum.Parse(raType, "Hours"))!;
    }

    private static object TransformToJ2000(Type coordinatesType, object coordinates)
    {
        var epochType = coordinatesType.Assembly.GetType("NINA.Astrometry.Epoch", throwOnError: true)!;
        return coordinatesType.GetMethod("Transform", [epochType])!
            .Invoke(coordinates, [Enum.Parse(epochType, "J2000")])!;
    }

    private static double GetDouble(object instance, string propertyName) =>
        (double)instance.GetType().GetProperty(propertyName)!.GetValue(instance)!;

    private static void SetProperty(object instance, string propertyName, object? value) =>
        instance.GetType().GetProperty(propertyName)!.SetValue(instance, value);

    private static object TaskFromResult(Type resultType, object result) =>
        typeof(Task).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(method => method.Name == nameof(Task.FromResult))
            .MakeGenericMethod(resultType)
            .Invoke(null, [result])!;

    private static object TaskFromException(Type resultType, Exception exception) =>
        typeof(Task).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(method => method.Name == nameof(Task.FromException) && method.IsGenericMethodDefinition)
            .MakeGenericMethod(resultType)
            .Invoke(null, [exception])!;

    private const string NinaDirectory = @"C:\Program Files\N.I.N.A. - Nighttime Imaging 'N' Astronomy";
}

internal static class NinaTestAssemblyResolver
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            const string ninaDirectory = @"C:\Program Files\N.I.N.A. - Nighttime Imaging 'N' Astronomy";
            var candidate = Path.Combine(ninaDirectory, $"{name.Name}.dll");
            return File.Exists(candidate)
                ? AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate)
                : null;
        };
    }
}
