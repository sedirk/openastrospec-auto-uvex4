using System.Reflection;
using NINA.Astrometry.Interfaces;
using NINA.Profile.Interfaces;
using UvexAdv.Nina.Plugin.SequenceItems;

namespace UvexAdv.Nina.Plugin.UiHarness;

/// <summary>The production target model and XAML, with inert profile/clock dependencies.</summary>
public static class NativeTargetEditorFixture
{
    public static UvexObservatoryCloseoutItem CreateCloseout(bool close)
    {
        // Rendering only: no host, runner, physical device or Profile writes.
        var night = new UvexNightSequenceContainer(Proxy<IProfileService>(), null!, null!, null!)
        { HomeBeforeParkOnFinalize = false };
        var root = new NINA.Sequencer.Container.SequenceRootContainer();
        var targetArea = new NINA.Sequencer.Container.TargetAreaContainer(); root.Add(targetArea);
        targetArea.Add(night);
        var end = new NINA.Sequencer.Container.EndAreaContainer(); root.Add(end);
        var card = new UvexObservatoryCloseoutItem { CloseAfterTargets = close }; end.Add(card);
        return card;
    }

    public static UvexTargetObservationContainer Create(string name, double ra = 90)
    {
        const string nina = @"C:\Program Files\N.I.N.A. - Nighttime Imaging 'N' Astronomy";
        try
        {
            System.Runtime.InteropServices.NativeLibrary.SetDllImportResolver(typeof(NINA.Astrometry.NOVAS).Assembly,
                (library, _, _) => library switch
                {
                    "NOVAS31lib.dll" => System.Runtime.InteropServices.NativeLibrary.Load(System.IO.Path.Combine(nina, "External", "x64", "NOVAS", library)),
                    "SOFAlib.dll" => System.Runtime.InteropServices.NativeLibrary.Load(System.IO.Path.Combine(nina, "External", "x64", "SOFA", library)),
                    _ => IntPtr.Zero
                });
        }
        catch (InvalidOperationException) { /* Another offline fixture owns the resolver. */ }
        var astrometry = Proxy<IAstrometrySettings>();
        var profile = Proxy<IProfile>((m, _) => m.Name == "get_AstrometrySettings" ? astrometry : Default(m.ReturnType));
        var profiles = Proxy<IProfileService>((m, _) => m.Name == "get_ActiveProfile" ? profile : Default(m.ReturnType));
        var values = new Dictionary<string, object?>();
        var accessor = Proxy<IPluginOptionsAccessor>((m, a) =>
        {
            if (m.Name.StartsWith("GetValue")) return values.TryGetValue((string)a[0]!, out var value) ? value : a[1];
            if (m.Name.StartsWith("SetValue")) { values[(string)a[0]!] = a[1]; return null; }
            return Default(m.ReturnType);
        });
        return new(profiles, Proxy<INighttimeCalculator>(), null!, null!, new UvexPluginSettings(profiles, accessor))
        {
            TargetName = name, RightAscensionDegrees = ra, DeclinationDegrees = 20,
            ScienceFrames = 2, MaximumScienceAttempts = 4, FixedExposureSeconds = 120,
            DurationMinutes = 20, FailurePolicy = SpectroscopyTargetFailurePolicy.SkipQualityFailure,
            UseRealMode = false,
        };
    }

    private static object? Default(Type type) => type == typeof(void) ? null : type.IsValueType ? Activator.CreateInstance(type) : null;
    private static T Proxy<T>(Func<MethodInfo, object?[], object?>? handler = null) where T : class
    {
        var proxy = DispatchProxy.Create<T, InterfaceProxy>();
        ((InterfaceProxy)(object)proxy).Handler = handler ?? ((m, _) => Default(m.ReturnType));
        return proxy;
    }
    private class InterfaceProxy : DispatchProxy
    {
        internal Func<MethodInfo, object?[], object?> Handler { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args ?? []);
    }
}
