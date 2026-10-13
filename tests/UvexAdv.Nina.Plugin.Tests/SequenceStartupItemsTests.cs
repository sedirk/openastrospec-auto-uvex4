using System.Reflection;
using NINA.Astrometry.Interfaces;
using NINA.Core.Model;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem.Connect;
using NINA.Sequencer.SequenceItem.SafetyMonitor;
using UvexAdv.Nina.Plugin.SequenceItems;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class SequenceStartupItemsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualNativeSafetyConnectIsReusableAndIdempotent(bool connected)
    {
        using var f = new Fixture();
        f.Connected = connected;
        var native = f.NativeConnect();
        Assert.Equal("Safety Monitor", ((ConnectEquipment)native.Clone()).SelectedDevice);
        await native.Execute(f.Progress, default);
        await native.Execute(f.Progress, default);
        Assert.Equal(connected ? 0 : 1, f.ConnectCalls);
        Assert.False(f.Settings.ObservationUseRealMode);
        var wait = new WaitUntilSafe(f.Monitor) { WaitInterval = TimeSpan.FromMilliseconds(5) };
        using var cancel = new CancellationTokenSource(40);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait.Execute(f.Progress, cancel.Token));
        Assert.False(wait.IsSafe); // A native connection never turns Unsafe into Safe.
    }

    [Fact]
    public async Task ActualNativeSafetyConnectPropagatesFailedConnection()
    {
        using var f = new Fixture(); f.ConnectResult = false;
        await Assert.ThrowsAnyAsync<Exception>(() => f.NativeConnect().Execute(f.Progress, default));
        Assert.Equal(1, f.ConnectCalls);
        Assert.False(f.Connected);
    }
    [Fact]
    public async Task DisabledCardsDoNotWriteProfileOrTouchHardware()
    {
        using var f = new Fixture();
        f.Mode.EnableRealMode = false; f.Safety.StartMonitoring = false;
        await f.Mode.Execute(f.Progress, default); await f.Safety.Execute(f.Progress, default);
        Assert.False(f.Settings.ObservationUseRealMode); Assert.Equal(0, f.ConnectCalls);
        Assert.False(((UvexSelectRealModeItem)f.Mode.Clone()).EnableRealMode);
    }

    [Fact]
    public async Task PreparationDeclaresPreflightOnlyExecutionStillRequiresActualModeSwitch()
    {
        using var f = new Fixture();
        Assert.True(SequenceStartupScope.HasRealPreparation(f.Target));
        Assert.False(f.Settings.ObservationUseRealMode);
        await f.Mode.Execute(f.Progress, default);
        Assert.True(f.Settings.ObservationUseRealMode);
        Assert.True(f.Target.UseRealMode);
        Assert.Equal(0, f.ConnectCalls);
        Assert.True(((UvexSelectRealModeItem)f.Mode.Clone()).EnableRealMode);
    }

    [Theory]
    [InlineData("worker")]
    [InlineData("uncommissioned")]
    [InlineData("simulation")]
    [InlineData("wrong-scope")]
    public async Task ModeCannotPromoteWorkerSimulationOrWrongScope(string fault)
    {
        using var f = new Fixture();
        if (fault == "worker") f.Settings.InstanceRole = NinaInstanceRole.PhotometryWorker;
        if (fault == "uncommissioned") f.Settings.RealModeCommissioned = false;
        if (fault == "simulation") f.Target.UseRealMode = false;
        if (fault == "wrong-scope") f.Night.Add(f.Mode);
        Assert.False(f.Mode.Validate());
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Mode.Execute(f.Progress, default));
        Assert.False(f.Settings.ObservationUseRealMode);
        Assert.Equal(0, f.ConnectCalls);
    }

    [Fact]
    public async Task ExistingNightBlocksBothStartupActions()
    {
        using var f = new Fixture();
        using var owner = await f.Host.ReserveNightAsync(new object(), false, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Mode.Execute(f.Progress, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Safety.Execute(f.Progress, default));
        Assert.False(f.Settings.ObservationUseRealMode); Assert.Equal(0, f.ConnectCalls);
    }

    [Theory]
    [InlineData(false, "AIWeatherSafetyMonitor")]
    [InlineData(true, "AIWeatherSafetyMonitor")]
    [InlineData(false, "ASCOM.Example.SafetyMonitor")]
    [InlineData(true, "ASCOM.Example.SafetyMonitor")]
    public async Task SafetyConnectIsIdempotentAndDoesNotClaimUnsafeIsSafe(bool connected, string identity)
    {
        using var f = new Fixture();
        f.Selection = f.Identity = identity;
        f.Connected = connected;
        await f.Safety.Execute(f.Progress, default);
        await f.Safety.Execute(f.Progress, default);
        Assert.Equal(connected ? 0 : 1, f.ConnectCalls);
        Assert.False(f.Settings.ObservationUseRealMode);
        Assert.True(((UvexStartSafetyMonitorItem)f.Safety.Clone()).StartMonitoring);
    }

    [Theory]
    [InlineData("selection")]
    [InlineData("blank-selection")]
    [InlineData("selection-changed")]
    [InlineData("wrong-connected")]
    [InlineData("connect-failed")]
    [InlineData("wrong-readback")]
    [InlineData("cancelled")]
    public async Task SafetyFailureCannotContinue(string fault)
    {
        using var f = new Fixture();
        if (fault == "selection") f.Selection = "No_Device";
        if (fault == "blank-selection") f.Selection = " ";
        if (fault == "selection-changed") f.DuringConnect = () => f.Selection = "another";
        if (fault == "wrong-connected") { f.Connected = true; f.Identity = "another"; }
        if (fault == "connect-failed") f.ConnectResult = false;
        if (fault == "wrong-readback") f.Identity = "another";
        var token = fault == "cancelled" ? new CancellationToken(true) : default;
        await Assert.ThrowsAnyAsync<Exception>(() => f.Safety.Execute(f.Progress, token));
        if (fault is "selection" or "blank-selection" or "wrong-connected" or "cancelled") Assert.Equal(0, f.ConnectCalls);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string leaseDirectory = Path.Combine(Path.GetTempPath(), "uvex-startup-tests", Guid.NewGuid().ToString("N"));
        public ObservationCoordinatorHost Host { get; }
        public UvexPluginSettings Settings { get; }
        public UvexSelectRealModeItem Mode { get; }
        public UvexStartSafetyMonitorItem Safety { get; }
        public UvexTargetObservationContainer Target { get; }
        public UvexNightSequenceContainer Night { get; }
        public IProgress<ApplicationStatus> Progress { get; } = new Progress<ApplicationStatus>();
        public bool Connected, ConnectResult = true;
        public string Selection = "AIWeatherSafetyMonitor", Identity = "AIWeatherSafetyMonitor";
        public int ConnectCalls;
        public Action? DuringConnect;
        public IProfileService Profiles { get; }
        public ISafetyMonitorMediator Monitor { get; }
        public ConnectEquipment NativeConnect() => new(Profiles, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, Monitor)
        { SelectedDevice = "Safety Monitor" };
        public Fixture()
        {
            Host = new(new SilentNotifier(), Path.Combine(leaseDirectory, "owner.lock"));
            var values = new Dictionary<string, object?>();
            var options = Proxy<IPluginOptionsAccessor>((m,a) => {
                if (m.Name.StartsWith("GetValue")) return values.TryGetValue((string)a[0]!,out var v) ? v : a[1];
                if (m.Name.StartsWith("SetValue")) { values[(string)a[0]!] = a[1]; return null; }
                return Default(m.ReturnType);
            });
            var safety = Proxy<ISafetyMonitorSettings>((m,_) => m.Name == "get_Id" ? Selection : Default(m.ReturnType));
            var astrometry = Proxy<IAstrometrySettings>((m,_) => Default(m.ReturnType));
            var profile = Proxy<IProfile>((m,_) => m.Name switch {
                "get_SafetyMonitorSettings" => safety,
                "get_CameraSettings" => Proxy<ICameraSettings>((cm,_) => cm.Name == "get_Id" ? "OFFLINE-CAMERA" : Default(cm.ReturnType)),
                "get_AstrometrySettings" => astrometry,
                _ => Default(m.ReturnType),
            });
            var profiles = Proxy<IProfileService>((m,_) => m.Name == "get_ActiveProfile" ? profile : Default(m.ReturnType));
            Profiles = profiles;
            Settings = new(profiles, options) { RealModeCommissioned = true, ObservationUseRealMode = false };
            var mediator = Proxy<ISafetyMonitorMediator>((m,_) => {
                if (m.Name == "Rescan") return Task.FromResult<IList<string>>(new[] { Selection });
                if (m.Name == "Connect") { ConnectCalls++; DuringConnect?.Invoke(); Connected = ConnectResult; return Task.FromResult(ConnectResult); }
                if (m.Name == "GetInfo") {
                    var info = Activator.CreateInstance(m.ReturnType)!;
                    m.ReturnType.GetProperty("Connected")!.SetValue(info, Connected);
                    m.ReturnType.GetProperty("DeviceId")!.SetValue(info, Identity);
                    m.ReturnType.GetProperty("IsSafe")!.SetValue(info, false);
                    return info;
                }
                throw new InvalidOperationException("Unexpected equipment method: " + m.Name);
            });
            Monitor = mediator;
            Mode = new(profiles, Host, Settings) { EnableRealMode = true };
            Safety = new(profiles, Host, mediator, Settings) { StartMonitoring = true };
            var root = new SequenceRootContainer();
            var start = new StartAreaContainer(); root.Add(start);
            var area = new TargetAreaContainer(); root.Add(area);
            Night = new(profiles, Host, null!, Settings); area.Add(Night);
            Target = new(profiles, Proxy<INighttimeCalculator>((m,_) => Default(m.ReturnType)), Host, null!, Settings) { UseRealMode = true }; Night.Add(Target);
            start.Add(Safety); start.Add(Mode);
        }
        public void Dispose()
        {
            Host.Dispose();
            if (Directory.Exists(leaseDirectory)) Directory.Delete(leaseDirectory, true);
        }
    }
    private sealed class SilentNotifier : IObservationAttentionNotifier
    {
        public void Notify(ObservationAttentionNotification n) { }
        public void ClearActiveIndicator() { }
        public void Dispose() { }
    }
    private static object? Default(Type t) => t == typeof(void) ? null : t.IsValueType ? Activator.CreateInstance(t) : null;
    private static T Proxy<T>(Func<MethodInfo, object?[], object?> handler) where T : class
    { var p = DispatchProxy.Create<T, TestProxy>(); ((TestProxy)(object)p).Handler = handler; return p; }
    private class TestProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[], object?> Handler { get; set; } = null!;
        protected override object? Invoke(MethodInfo? m, object?[]? a) => Handler(m!, a ?? []);
    }
}
