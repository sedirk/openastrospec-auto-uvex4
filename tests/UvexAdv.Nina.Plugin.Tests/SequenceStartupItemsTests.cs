using System.Reflection;
using NINA.Astrometry.Interfaces;
using NINA.Core.Model;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Container;
using UvexAdv.Nina.Plugin.SequenceItems;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class SequenceStartupItemsTests
{
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
    [InlineData(false)]
    [InlineData(true)]
    public async Task SafetyConnectIsIdempotentAndDoesNotClaimUnsafeIsSafe(bool connected)
    {
        using var f = new Fixture();
        f.Connected = connected;
        await f.Safety.Execute(f.Progress, default);
        await f.Safety.Execute(f.Progress, default);
        Assert.Equal(connected ? 0 : 1, f.ConnectCalls);
        Assert.False(f.Settings.ObservationUseRealMode);
        Assert.True(((UvexStartSafetyMonitorItem)f.Safety.Clone()).StartMonitoring);
    }

    [Theory]
    [InlineData("selection")]
    [InlineData("wrong-connected")]
    [InlineData("connect-failed")]
    [InlineData("wrong-readback")]
    [InlineData("cancelled")]
    public async Task SafetyFailureCannotContinue(string fault)
    {
        using var f = new Fixture();
        if (fault == "selection") f.Selection = "No_Device";
        if (fault == "wrong-connected") { f.Connected = true; f.Identity = "another"; }
        if (fault == "connect-failed") f.ConnectResult = false;
        if (fault == "wrong-readback") f.Identity = "another";
        var token = fault == "cancelled" ? new CancellationToken(true) : default;
        await Assert.ThrowsAnyAsync<Exception>(() => f.Safety.Execute(f.Progress, token));
        if (fault is "selection" or "wrong-connected" or "cancelled") Assert.Equal(0, f.ConnectCalls);
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
                "get_AstrometrySettings" => astrometry,
                _ => Default(m.ReturnType),
            });
            var profiles = Proxy<IProfileService>((m,_) => m.Name == "get_ActiveProfile" ? profile : Default(m.ReturnType));
            Settings = new(profiles, options) { RealModeCommissioned = true, ObservationUseRealMode = false };
            var mediator = Proxy<ISafetyMonitorMediator>((m,_) => {
                if (m.Name == "Connect") { ConnectCalls++; Connected = ConnectResult; return Task.FromResult(ConnectResult); }
                if (m.Name == "GetInfo") {
                    var info = Activator.CreateInstance(m.ReturnType)!;
                    m.ReturnType.GetProperty("Connected")!.SetValue(info, Connected);
                    m.ReturnType.GetProperty("DeviceId")!.SetValue(info, Identity);
                    m.ReturnType.GetProperty("IsSafe")!.SetValue(info, false);
                    return info;
                }
                throw new InvalidOperationException("Unexpected equipment method: " + m.Name);
            });
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
