using System.Reflection;
using NINA.Profile.Interfaces;
using NINA.Astrometry.Interfaces;
using NINA.Core.Model;
using UvexAdv.Nina.Plugin.SequenceItems;
using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class NativeSequencePlanTests
{
    [Fact]
    public void TargetPlansAreIndependentImmutableAndDoNotWriteTheProfile()
    {
        var settings = Settings();
        var originalCount = settings.AtrScienceFrameCount;
        var solver = new PlateSolverRunConfiguration("primary", "blind", "primary-type", "blind-type",
            10, 10, 2, 500, true, 0.1, 5, 3, "", "", "", "", "", "", "", "");
        var original = RealRunConfiguration.Capture(settings, solver);
        var first = original.WithSequencePlan(new(3, 6, 600, true));
        var second = original.WithSequencePlan(new(8, 12, 120, true));
        Assert.Equal(3, first.Atr.ScienceFrameCount);
        Assert.Equal(8, second.Atr.ScienceFrameCount);
        Assert.Equal(new[] { 600d }, first.Atr.ExposureLadderSeconds);
        Assert.Equal(600, first.Atr.ProbeExposureSeconds);
        Assert.Equal(originalCount, settings.AtrScienceFrameCount);
        Assert.NotEqual(first.ActionConfigurationSha256, second.ActionConfigurationSha256);
        Assert.True(first.MatchesCurrentProfile(settings, solver, out _));
        settings.Offset++;
        Assert.False(first.MatchesCurrentProfile(settings, solver, out _));
    }

    [Theory]
    [InlineData(0, 1, 0)] [InlineData(2, 1, 600)] [InlineData(1, 2, -1)]
    [InlineData(1, 2, double.NaN)] [InlineData(1, 2, 4000)]
    public void InvalidTargetsCannotCreateAuthority(int frames, int attempts, double seconds) =>
        Assert.NotEmpty(new NativeSequencePlan(frames, attempts, seconds).Validate());

    [Theory]
    [InlineData("G3_CATALOG_SHORT_POSITION_UNCONFIRMED", true)]
    [InlineData("G3_MOTION_TERMINAL_HANDOFF_INCONSISTENT", false)]
    [InlineData("G3_MOTION_CRASH_RETURN_BLOCKED", false)]
    [InlineData("PHD2_GUIDING_FRAME_TIMEOUT", false)]
    [InlineData("SAFETY_UNSAFE", false)] [InlineData("RUN_MANIFEST_WRITE_FAILED", false)]
    [InlineData("unknown", false)]
    public void SkipPolicyNeverGuessesFromText(string code, bool expected) =>
        Assert.Equal(expected, NativeSequencePlan.IsSkippableQualityFailure(code));

    [Fact]
    public void DeadlineAndDawnAreCheckedAtCallerBoundariesWithoutTimersOrCancellation()
    {
        var now = DateTimeOffset.UtcNow;
        var plan = new NativeSequencePlan(3, 6, 600, true, now.AddSeconds(1), true, -12);
        Assert.Null(plan.StopGate(now, _ => -13));
        Assert.Equal("NIGHT_DAWN_REACHED", plan.StopGate(now, _ => -12)?.Code);
        Assert.Equal("NIGHT_DEADLINE_REACHED", plan.StopGate(now.AddSeconds(1), _ => -13)?.Code);
        Assert.Null((plan with { StopAtDawn = false, DeadlineUtc = null }).StopGate(now, _ => 60));
    }

    [Fact]
    public async Task NightReservationExcludesFocusAndOtherStartsEvenBetweenTargets()
    {
        using var host = new ObservationCoordinatorHost(new SilentNotifier());
        using (await host.ReserveNightAsync(new object(), false, CancellationToken.None))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.RunFocusPreparationAsync(
                () => Task.FromResult(1), CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.ReserveNightAsync(new object(), false, CancellationToken.None));
        }
        using var nextNight = await host.ReserveNightAsync(new object(), false, CancellationToken.None);
    }

    [Theory]
    [InlineData("complete")]
    [InlineData("deadline")]
    [InlineData("cancel")]
    [InlineData("target-interrupt")]
    public async Task TwoTargetsRunThroughNativeSequentialStrategyAndSharedCoordinatorWithoutHardware(string mode)
    {
        const string nina = @"C:\Program Files\N.I.N.A. - Nighttime Imaging 'N' Astronomy";
        try { System.Runtime.InteropServices.NativeLibrary.SetDllImportResolver(typeof(NINA.Astrometry.NOVAS).Assembly,
            (name, _, _) => name switch
            {
                "NOVAS31lib.dll" => System.Runtime.InteropServices.NativeLibrary.Load(Path.Combine(nina, "External", "x64", "NOVAS", name)),
                "SOFAlib.dll" => System.Runtime.InteropServices.NativeLibrary.Load(Path.Combine(nina, "External", "x64", "SOFA", name)),
                _ => IntPtr.Zero
            }); }
        catch (InvalidOperationException) { /* Another read-only astrometry fixture owns this resolver. */ }
        var settings = Settings();
        var astrometry = Proxy<IAstrometrySettings>();
        var profile = Proxy<IProfile>((m, _) => m.Name == "get_AstrometrySettings" ? astrometry : Default(m.ReturnType));
        var profiles = Proxy<IProfileService>((m, _) => m.Name == "get_ActiveProfile" ? profile : Default(m.ReturnType));
        var notifier = new SilentNotifier();
        using var host = new ObservationCoordinatorHost(notifier);
        notifier.OnNotify = notification =>
        {
            if (notification.Severity != ObservationAttentionSeverity.Success) return;
            var dashboard = host.Dashboard;
            Assert.Equal(ObservationRunState.Completed, dashboard.Run.State);
            // Check the file at notification time, not just after Execute returns.
            using var manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllText(dashboard.ManifestPath!));
            Assert.Equal("Completed", manifest.RootElement.GetProperty("terminalState").GetString());
            Assert.Equal(dashboard.Run.ObservationRunId, manifest.RootElement.GetProperty("observationRunId").GetString());
            Assert.Contains(dashboard.LockedPlan!.Target.Name, notification.Body);
        };
        var night = new UvexNightSequenceContainer(profiles, host, null!, settings) { StopAtDawn = false };
        var now = DateTimeOffset.UtcNow;
        var jd = now.ToUnixTimeMilliseconds() / 86400000d + 2440587.5;
        var ra = ((280.46061837 + 360.98564736629 * (jd - 2451545) + 120) % 360 + 360) % 360;
        for (var i = 0; i < 2; i++)
        {
            night.Add(new UvexTargetObservationContainer(profiles, Proxy<INighttimeCalculator>(), host, null!, settings)
            {
                TargetName = $"Native simulation {i}", CatalogId = $"SIM-{i}", NightSetupId = "SIM-NIGHT",
                UseRealMode = false, SiteLatitudeDegrees = 33, SiteLongitudeDegreesEast = 120,
                RightAscensionDegrees = ra, DeclinationDegrees = 33, DurationMinutes = 1,
                HorizonMinimumDegrees = 0, HorizonStartMarginDegrees = 0, HorizonContinueMarginDegrees = 0,
                ExpectedAtrCameraId = "SIM-ATR", ExpectedG3ProfileName = "SIM-G3", ExpectedQhyCameraId = "SIM-QHY",
                ScienceFrames = i + 1, MaximumScienceAttempts = 4, SimulationStageMilliseconds = 250,
            });
        }
        var clone = (UvexNightSequenceContainer)night.Clone();
        Assert.Equal(2, clone.Items.Count);
        Assert.Equal(1, ((UvexTargetObservationContainer)clone.Items[0]).ScienceFrames);
        Assert.Equal(2, ((UvexTargetObservationContainer)clone.Items[1]).ScienceFrames);
        // The exact settings used by N.I.N.A. SequenceJsonConverter.Serialize.
        var serialized = Newtonsoft.Json.JsonConvert.SerializeObject(night, new Newtonsoft.Json.JsonSerializerSettings
        {
            TypeNameHandling = Newtonsoft.Json.TypeNameHandling.All,
            PreserveReferencesHandling = Newtonsoft.Json.PreserveReferencesHandling.All,
        });
        Assert.Contains("ScienceFrames", serialized);
        Assert.Contains("StopAtDawn", serialized);
        var sequencerFactory = Proxy<NINA.Sequencer.ISequencerFactory>((m, _) => m.Name switch
        {
            "GetContainer" when m.GetGenericArguments()[0] == typeof(UvexNightSequenceContainer) => new UvexNightSequenceContainer(profiles, host, null!, settings),
            "GetContainer" => new UvexTargetObservationContainer(profiles, Proxy<INighttimeCalculator>(), host, null!, settings),
            "GetItem" => new ObservationStageMarkerItem(),
            _ => Default(m.ReturnType)
        });
        var converter = new NINA.Sequencer.Serialization.SequenceJsonConverter(sequencerFactory);
        night = Assert.IsType<UvexNightSequenceContainer>(converter.Deserialize(converter.Serialize(night)));
        Assert.Equal(2, night.Items.Count);
        Assert.All(night.Items.Cast<UvexTargetObservationContainer>(), x => Assert.Equal(11, x.Items.Count));
        Assert.Equal(2, ((UvexTargetObservationContainer)night.Items[1]).ScienceFrames);
        if (mode == "deadline") night.EndAtIso8601 = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O");
        using var cancel = new CancellationTokenSource();
        if (mode == "cancel") cancel.CancelAfter(600);
        var execution = night.Execute(new Progress<ApplicationStatus>(), cancel.Token);
        if (mode == "target-interrupt")
        {
            await Task.Delay(600);
            await ((UvexTargetObservationContainer)night.Items[0]).Interrupt();
        }
        try { await execution.WaitAsync(TimeSpan.FromSeconds(20)); }
        catch (OperationCanceledException) when (mode is "cancel" or "target-interrupt") { }
        if (mode != "complete")
        {
            Assert.DoesNotContain(notifier.Notifications, x => x.Severity == ObservationAttentionSeverity.Success);
            if (mode is "cancel" or "target-interrupt")
            {
                Assert.Equal(ObservationRunState.Cancelled, host.Dashboard.Run.State);
                Assert.Contains("不执行机械收口", night.NightState);
            }
            else
            {
                Assert.Equal(ObservationRunState.Faulted, host.Dashboard.Run.State);
                Assert.Contains(host.Dashboard.Gates.Values, x => x.Code == "NIGHT_DEADLINE_REACHED");
            }
            Assert.NotEqual("完成", ((UvexTargetObservationContainer)night.Items[1]).LastOutcome);
            return;
        }
        Assert.Equal(ObservationRunState.Completed, host.Dashboard.Run.State);
        var successes = notifier.Notifications.Where(x => x.Severity == ObservationAttentionSeverity.Success).ToArray();
        Assert.Equal(2, successes.Length);
        Assert.Equal(2, successes.Select(x => x.Fingerprint).Distinct().Count());
        Assert.Contains("Native simulation 0", successes[0].Body);
        Assert.Contains("Native simulation 1", successes[1].Body);
        Assert.All(night.Items.Cast<UvexTargetObservationContainer>(), x => Assert.Equal("完成", x.LastOutcome));
        Assert.Contains("模拟结束", night.NightState);
        Assert.Equal(2, System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(night.JournalPath))
            .RootElement.GetProperty("outcomes").EnumerateArray().Count(x => x.TryGetProperty("outcome", out var v) && v.GetString() == "Completed"));
    }

    [Fact]
    public void RecoveredPriorRunIsClosedBeforeReleasingItsInMemoryBudget()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "RealObservationStageRunner.cs"));
        // Search the unique new event instead of depending on platform line endings.
        var marker = source.IndexOf("\"g3-prior-run-recovery-closed\"", StringComparison.Ordinal);
        Assert.True(marker > 0);
        var boundary = source[marker..source.IndexOf("lastG3Field = null;", marker, StringComparison.Ordinal)];
        Assert.Contains("sourcePath: returned.Path", boundary);
        Assert.Contains("durableG3AcquisitionMotion = null", boundary);
        Assert.Contains("previousRunFineAcquisitionStartedUtc", boundary);
        Assert.DoesNotContain("ObservationRunId = context.Plan.ObservationRunId", boundary);
        Assert.DoesNotContain("PersistG3AcquisitionMotionAsync", boundary);
        Assert.Contains("if (!returned.ReturnedToOrigin)", source[..marker]);
    }

    private sealed class SilentNotifier : IObservationAttentionNotifier
    {
        public List<ObservationAttentionNotification> Notifications { get; } = new();
        public Action<ObservationAttentionNotification>? OnNotify { get; set; }
        public void Notify(ObservationAttentionNotification notification)
        {
            Notifications.Add(notification);
            OnNotify?.Invoke(notification);
        }
        public void ClearActiveIndicator() { }
        public void Dispose() { }
    }

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
