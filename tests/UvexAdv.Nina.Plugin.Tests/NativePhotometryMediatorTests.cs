using System.Reflection;
using NINA.Core.Model;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;
using UvexAdv.Qhy.Core;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class NativePhotometryMediatorTests
{
    [Fact]
    public async Task NativeSequenceCaptureUsesItsActualCameraOwnerAndNeverStealsTheBlock()
    {
        var owner = new object(); var registrations = 0; var releases = 0;
        var camera = Proxy<ICameraMediator>((m, args) => m.Name switch
        {
            "IsFreeToCapture" => ReferenceEquals(args[0], owner),
            "RegisterCaptureBlock" => ++registrations,
            "ReleaseCaptureBlock" => ++releases,
            _ => Default(m.ReturnType),
        });
        var settings = Settings();
        var adapter = new NinaPhotometryCameraAdapter(Proxy<IProfileService>(), camera,
            Proxy<IFilterWheelMediator>(), Proxy<IFocuserMediator>(), Proxy<IImagingMediator>(), Proxy<IImageSaveMediator>(),
            settings, () => throw new InvalidOperationException("fixture-checkpoint"), new Progress<ApplicationStatus>(), () => owner);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.CaptureWithCheckpointAsync(Frame(), _ => Task.CompletedTask, CancellationToken.None));
        Assert.Equal("fixture-checkpoint", error.Message);
        Assert.Equal(0, registrations); Assert.Equal(0, releases);
    }

    [Fact]
    public async Task DirectWorkerReleasesItsOwnCameraBlockEvenWhenValidationFails()
    {
        var actions = new List<string>();
        var camera = Proxy<ICameraMediator>((m, _) =>
        {
            actions.Add(m.Name);
            return m.Name == "IsFreeToCapture" ? true : Default(m.ReturnType);
        });
        var adapter = new NinaPhotometryCameraAdapter(Proxy<IProfileService>(), camera,
            Proxy<IFilterWheelMediator>(), Proxy<IFocuserMediator>(), Proxy<IImagingMediator>(), Proxy<IImageSaveMediator>(),
            Settings(), () => throw new InvalidOperationException("fixture-reject"), new Progress<ApplicationStatus>());
        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.CaptureWithCheckpointAsync(Frame(), _ => Task.CompletedTask, CancellationToken.None));
        Assert.Equal(["IsFreeToCapture", "RegisterCaptureBlock", "ReleaseCaptureBlock"], actions);
    }

    [Fact]
    public async Task UnrelatedNativeCaptureBlockPreventsAnyOwnerOrImagingCall()
    {
        var calls = 0;
        var camera = Proxy<ICameraMediator>((m, _) => m.Name == "IsFreeToCapture" ? false : throw new Exception("unexpected camera call"));
        var adapter = new NinaPhotometryCameraAdapter(Proxy<IProfileService>(), camera,
            Proxy<IFilterWheelMediator>(), Proxy<IFocuserMediator>(), Proxy<IImagingMediator>(), Proxy<IImageSaveMediator>(),
            Settings(), () => calls++, new Progress<ApplicationStatus>());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.CaptureWithCheckpointAsync(Frame(), _ => Task.CompletedTask, CancellationToken.None));
        Assert.Contains("PHOTOMETRY_CAMERA_BUSY", error.Message); Assert.Equal(0, calls);
    }

    [Fact]
    public void PhotometrySwitchIsProfileScopedAndPartOfTheImmutableSharedConfiguration()
    {
        var settings = Settings();
        var solver = new PlateSolverRunConfiguration("primary", "blind", "primary-type", "blind-type",
            10, 10, 2, 500, true, 0.1, 5, 3, "", "", "", "", "", "", "", "");
        var before = RealRunConfiguration.Capture(settings, solver);
        Assert.True(before.Qhy.SynchronizedPhotometryEnabled);
        settings.SynchronizedPhotometryEnabled = false;
        var after = RealRunConfiguration.Capture(settings, solver);
        Assert.False(after.Qhy.SynchronizedPhotometryEnabled);
        Assert.NotEqual(before.ActionConfigurationSha256, after.ActionConfigurationSha256);
        Assert.Equal(before.G3.SaturationAdu, after.G3.SaturationAdu);
        Assert.Equal(before.Qhy.AcquisitionExposureLadderSeconds, after.Qhy.AcquisitionExposureLadderSeconds);
    }

    private static QhyFrameSettings Frame() => new(1, 0, 0, FilterName: "L");
    private static UvexPluginSettings Settings()
    {
        var values = new Dictionary<string, object?>();
        var accessor = Proxy<IPluginOptionsAccessor>((m, a) =>
        {
            if (m.Name.StartsWith("GetValue")) return values.TryGetValue((string)a[0]!, out var v) ? v : a[1];
            if (m.Name.StartsWith("SetValue")) { values[(string)a[0]!] = a[1]; return null; }
            return Default(m.ReturnType);
        });
        var astrometry = Proxy<IAstrometrySettings>();
        var profile = Proxy<IProfile>((m, _) => m.Name == "get_AstrometrySettings" ? astrometry : Default(m.ReturnType));
        return new(Proxy<IProfileService>((m, _) => m.Name == "get_ActiveProfile" ? profile : Default(m.ReturnType)), accessor);
    }
    private static object? Default(Type t) => t == typeof(void) ? null : t.IsValueType ? Activator.CreateInstance(t) : null;
    private static T Proxy<T>(Func<MethodInfo, object?[], object?>? handler = null) where T : class
    {
        var proxy = DispatchProxy.Create<T, InterfaceProxy>();
        ((InterfaceProxy)(object)proxy).Handler = handler ?? ((m, _) => Default(m.ReturnType));
        return proxy;
    }
    private class InterfaceProxy : DispatchProxy
    {
        internal Func<MethodInfo, object?[], object?> Handler { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args ?? []);
    }
}
