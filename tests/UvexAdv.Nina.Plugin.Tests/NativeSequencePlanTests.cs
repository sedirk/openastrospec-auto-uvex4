using System.Reflection;
using NINA.Sequencer.Container;
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
    public void ExposureSelectorUsesLegacySecondsContractAndSurvivesCloneAndNativeSave()
    {
        InitializeAstrometry();
        var settings = Settings();
        var savedLadder = settings.AtrExposureLadderSecondsCsv;
        var profile = Proxy<IProfile>((m, _) => m.Name == "get_AstrometrySettings" ? Proxy<IAstrometrySettings>() : Default(m.ReturnType));
        var profiles = Proxy<IProfileService>((m, _) => m.Name == "get_ActiveProfile" ? profile : Default(m.ReturnType));
        using var host = new ObservationCoordinatorHost(new SilentNotifier());
        var target = new UvexTargetObservationContainer(profiles, Proxy<INighttimeCalculator>(), host, null!, settings);
        Assert.Equal(SpectroscopyExposureMode.Automatic, target.ExposureMode);
        target.FixedExposureSeconds = 240;
        Assert.Equal(SpectroscopyExposureMode.Fixed, target.ExposureMode);
        var changes = new List<string?>();
        target.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        target.ExposureMode = SpectroscopyExposureMode.Automatic;
        Assert.Equal(0, target.FixedExposureSeconds);
        Assert.False(target.IsFixedExposure);
        Assert.Contains("AUTO", target.TargetSummary);
        Assert.Contains(nameof(target.ExposureMode), changes);
        Assert.Contains(nameof(target.IsFixedExposure), changes);
        var clone = (UvexTargetObservationContainer)target.Clone();
        clone.ExposureMode = SpectroscopyExposureMode.Fixed;
        Assert.Equal(240, clone.FixedExposureSeconds);
        Assert.Equal(0, target.FixedExposureSeconds);
        var factory = Proxy<NINA.Sequencer.ISequencerFactory>((m, _) => m.Name switch
        {
            "GetContainer" => (UvexTargetObservationContainer)clone.Clone(),
            "GetItem" => new ObservationStageMarkerItem(), _ => Default(m.ReturnType)
        });
        var converter = new NINA.Sequencer.Serialization.SequenceJsonConverter(factory);
        foreach (var source in new[] { target, clone })
        {
            var json = converter.Serialize(source);
            Assert.DoesNotContain("\"ExposureMode\"", json);
            var restored = Assert.IsType<UvexTargetObservationContainer>(converter.Deserialize(json));
            Assert.Equal(source.ExposureMode, restored.ExposureMode);
            Assert.Equal(source.FixedExposureSeconds, restored.FixedExposureSeconds);
        }
        var legacy = Newtonsoft.Json.Linq.JObject.Parse(converter.Serialize(clone));
        legacy.Remove(nameof(clone.FixedExposureSeconds));
        Assert.Equal(SpectroscopyExposureMode.Automatic,
            Assert.IsType<UvexTargetObservationContainer>(converter.Deserialize(legacy.ToString())).ExposureMode);
        Assert.Equal(savedLadder, settings.AtrExposureLadderSecondsCsv);
        using var busy = new CancellationTokenSource();
        typeof(UvexTargetObservationContainer).GetField("targetLifetime", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, busy);
        Assert.Throws<InvalidOperationException>(() => target.ExposureMode = SpectroscopyExposureMode.Fixed);
        Assert.Equal(0, target.FixedExposureSeconds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task NativeNightWorkerExecutionWithBoundWpfButtonsDoesNotCrash(bool cancel) => RunOnDispatcherAsync(async () =>
    {
        InitializeAstrometry();
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        var settings = Settings();
        var astrometry = Proxy<IAstrometrySettings>();
        var profile = Proxy<IProfile>((m, _) => m.Name == "get_AstrometrySettings" ? astrometry : Default(m.ReturnType));
        var profiles = Proxy<IProfileService>((m, _) => m.Name == "get_ActiveProfile" ? profile : Default(m.ReturnType));
        using var host = new ObservationCoordinatorHost(new SilentNotifier());
        using var cancellation = new CancellationTokenSource();
        var night = new UvexNightSequenceContainer(profiles, host, null!, settings) { StopAtDawn = false };
        var runner = new UiBoundRunner(() =>
        {
            Assert.False(night.CanEditPlan);
            Assert.All(night.Items.Cast<UvexTargetObservationContainer>(), t => Assert.False(t.CanEditTargetIdentity));
            if (cancel) cancellation.Cancel();
        });
        var target = new UvexTargetObservationContainer(profiles, Proxy<INighttimeCalculator>(), host, null!, settings, () => runner)
        {
            TargetName = "Synthetic UI regression", NightSetupId = "SIM-NIGHT", UseRealMode = false,
            SiteLatitudeDegrees = 33, SiteLongitudeDegreesEast = 120, RightAscensionDegrees = 0,
            DeclinationDegrees = 89, DurationMinutes = 1, HorizonMinimumDegrees = 0,
            HorizonStartMarginDegrees = 0, HorizonContinueMarginDegrees = 0,
            ExpectedAtrCameraId = "SIM-ATR", ExpectedG3ProfileName = "SIM-G3", ExpectedQhyCameraId = "SIM-QHY",
            ScienceFrames = 1, MaximumScienceAttempts = 2,
        };
        night.Add(target);
        var commands = new[] { target.DropTargetCommand, target.CoordsFromPlanetariumCommand, target.CoordsToFramingCommand };
        // Actual WPF Buttons subscribe through CanExecuteChangedEventManager:
        // this is the missing condition in the earlier headless regressions.
        var buttons = commands.Select(command => new System.Windows.Controls.Button { Command = command }).ToArray();
        var notifications = 0;
        var wrongThread = false;
        foreach (var command in commands)
            command.CanExecuteChanged += (_, _) => { notifications++; wrongThread |= !dispatcher.CheckAccess(); };
        var execution = Task.Run(() => night.Execute(new Progress<ApplicationStatus>(), cancellation.Token));
        try { await execution.WaitAsync(TimeSpan.FromSeconds(20)); }
        catch (OperationCanceledException) when (cancel) { }
        await dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Assert.False(wrongThread);
        Assert.True(notifications >= 6);
        Assert.False(night.IsActive);
        Assert.True(target.CanEditTargetIdentity);
        Assert.True(runner.Calls > 0);
        if (!cancel) Assert.Equal("完成", target.LastOutcome);
        else
        {
            Assert.NotEqual("完成", target.LastOutcome);
            Assert.Contains("取消", night.NightState);
            Assert.True(cancellation.IsCancellationRequested);
        }
        using var next = await host.ReserveNightAsync(new object(), false, CancellationToken.None);
        GC.KeepAlive(buttons);
    });

    [Fact]
    public async Task NativeNightInitializationFailureReleasesLifetimeAndReservation()
    {
        InitializeAstrometry();
        var settings = Settings();
        var astrometry = Proxy<IAstrometrySettings>();
        var profile = Proxy<IProfile>((m, _) => m.Name == "get_AstrometrySettings" ? astrometry : Default(m.ReturnType));
        var profiles = Proxy<IProfileService>((m, _) => m.Name == "get_ActiveProfile" ? profile : Default(m.ReturnType));
        using var host = new ObservationCoordinatorHost(new SilentNotifier());
        var night = new UvexNightSequenceContainer(profiles, host, null!, settings) { StopAtDawn = false };
        night.Add(new UvexTargetObservationContainer(profiles, Proxy<INighttimeCalculator>(), host, null!, settings));
        var thrown = false;
        night.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(night.CanEditPlan) && !thrown)
            { thrown = true; throw new InvalidOperationException("Synthetic initialization failure"); }
        };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => night.Execute(new Progress<ApplicationStatus>(), CancellationToken.None));
        Assert.Equal("Synthetic initialization failure", error.Message);
        Assert.False(night.IsActive);
        Assert.True(night.CanEditPlan);
        await night.Interrupt(); // No disposed cancellation source retained.
        using var next = await host.ReserveNightAsync(new object(), false, CancellationToken.None);
    }

    private sealed class UiBoundRunner(Action onStage) : ObservationStageRunnerBase
    {
        public int Calls { get; private set; }
        public override Task<StageResult> ExecuteStageAsync(ObservationStage stage, ObservationContext context, CancellationToken token)
        {
            Calls++; onStage(); token.ThrowIfCancellationRequested();
            return Task.FromResult(new StageResult(GateResult.Pass("SIM_UI", "No hardware.")));
        }
    }

    [Fact]
    public async Task ReusedNightAfterCancellationResetsRunningJournalAndHandsOverProtection()
    {
        InitializeAstrometry();
        var settings = Settings();
        var profile = Proxy<IProfile>((m, _) => m.Name == "get_AstrometrySettings" ? Proxy<IAstrometrySettings>() : Default(m.ReturnType));
        var profiles = Proxy<IProfileService>((m, _) => m.Name == "get_ActiveProfile" ? profile : Default(m.ReturnType));
        using var host = new ObservationCoordinatorHost(new SilentNotifier());
        var night = new UvexNightSequenceContainer(profiles, host, null!, settings)
        { StopAtDawn = false, HoldOpenAfterTargets = true, EndAtIso8601 = DateTimeOffset.UtcNow.AddMinutes(5).ToString("O") };
        UvexTargetObservationContainer Target(Action onStage) => new(profiles, Proxy<INighttimeCalculator>(), host, null!, settings,
            () => new UiBoundRunner(onStage))
        {
            TargetName = "Synthetic reuse", CatalogId = "SIM", NightSetupId = "SIM-NIGHT", UseRealMode = false,
            SiteLatitudeDegrees = 33, SiteLongitudeDegreesEast = 120, RightAscensionDegrees = 0, DeclinationDegrees = 89,
            DurationMinutes = 1, HorizonMinimumDegrees = 0, HorizonStartMarginDegrees = 0, HorizonContinueMarginDegrees = 0,
            ExpectedAtrCameraId = "SIM-ATR", ExpectedG3ProfileName = "SIM-G3", ExpectedQhyCameraId = "SIM-QHY", ScienceFrames = 1, MaximumScienceAttempts = 2,
        };
        night.Add(Target(() => night.Interrupt().GetAwaiter().GetResult()));
        var cancelled = await Record.ExceptionAsync(() => night.Execute(new Progress<ApplicationStatus>(), default));
        if (cancelled is not null) Assert.IsAssignableFrom<OperationCanceledException>(cancelled);
        Assert.Equal(ObservationRunState.Cancelled, host.Dashboard.Run.State);
        Assert.Contains("已取消", night.NightState);
        var oldJournal = night.JournalPath;
        var oldBytes = await File.ReadAllBytesAsync(oldJournal);
        var oldGuard = Assert.IsType<NightSafetyStandby>(host.SafetyStandby);
        night.Items.Clear();
        var checkedRunning = false;
        night.Add(Target(() =>
        {
            using var j = System.Text.Json.JsonDocument.Parse(File.ReadAllText(night.JournalPath));
            Assert.Equal("Running", j.RootElement.GetProperty("phase").GetString());
            Assert.DoesNotContain("取消", j.RootElement.GetProperty("state").GetString()!);
            Assert.Empty(j.RootElement.GetProperty("outcomes").EnumerateArray());
            checkedRunning = true;
        }));
        await night.Execute(new Progress<ApplicationStatus>(), default).WaitAsync(TimeSpan.FromSeconds(10));
        await oldGuard.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(checkedRunning); Assert.Equal("Transferred", oldGuard.Snapshot.Phase);
        Assert.NotEqual(oldJournal, night.JournalPath); Assert.Equal(oldBytes, await File.ReadAllBytesAsync(oldJournal));
        var nextGuard = Assert.IsType<NightSafetyStandby>(host.SafetyStandby);
        Assert.NotSame(oldGuard, nextGuard);
        night.EndNightCommand.Execute(null); await nextGuard.Completion.WaitAsync(TimeSpan.FromSeconds(3));
    }

    private static Task RunOnDispatcherAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try { await action(); completion.TrySetResult(); }
                catch (Exception ex) { completion.TrySetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Background); }
            });
            System.Windows.Threading.Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void NativeTargetDropChangesOnlyDetachedIdentityAndKeepsPerTargetPlan()
    {
        InitializeAstrometry();
        var settings = Settings();
        var astrometry = Proxy<IAstrometrySettings>();
        var profile = Proxy<IProfile>((m, _) => m.Name == "get_AstrometrySettings" ? astrometry : Default(m.ReturnType));
        var profiles = Proxy<IProfileService>((m, _) => m.Name == "get_ActiveProfile" ? profile : Default(m.ReturnType));
        using var host = new ObservationCoordinatorHost(new SilentNotifier());
        var destination = new UvexTargetObservationContainer(profiles, Proxy<INighttimeCalculator>(), host, null!, settings)
        {
            TargetName = "Original", RightAscensionDegrees = 90, DeclinationDegrees = 20,
            ScienceFrames = 3, FixedExposureSeconds = 300, ForceGuideCalibration = true,
            DurationMinutes = 37, MaximumScienceAttempts = 8,
            FailurePolicy = SpectroscopyTargetFailurePolicy.SkipQualityFailure, HorizonMinimumDegrees = 41,
            CatalogId = "OLD", CatalogMetadata = new("Original", "OLD", 90, 20, "synthetic", "star", 8, DateTimeOffset.UtcNow)
        };
        var source = (UvexTargetObservationContainer)destination.Clone();
        source.TargetName = "Replacement";
        source.Target.InputCoordinates = new NINA.Astrometry.InputCoordinates(new NINA.Astrometry.Coordinates(
            105, -0.5, NINA.Astrometry.Epoch.J2000, NINA.Astrometry.Coordinates.RAType.Degrees));
        source.ScienceFrames = 9; source.ForceGuideCalibration = false;
        var drop = new NINA.Sequencer.DragDrop.DropIntoParameters(new NINA.Sequencer.TargetSequenceContainer(profiles, source));
        Assert.True(destination.DropTargetCommand.CanExecute(drop));
        Assert.True(destination.TryImportDroppedTarget(drop));
        Assert.Equal("Replacement", destination.TargetName);
        Assert.Equal(105, destination.RightAscensionDegrees); Assert.Equal(-0.5, destination.DeclinationDegrees);
        Assert.True(destination.Target.InputCoordinates.NegativeDec);
        Assert.Equal(NINA.Astrometry.Epoch.J2000, destination.Target.InputCoordinates.Coordinates.Epoch);
        Assert.Equal(3, destination.ScienceFrames); Assert.Equal(300, destination.FixedExposureSeconds);
        Assert.True(destination.ForceGuideCalibration); Assert.Equal(37, destination.DurationMinutes);
        Assert.Equal(8, destination.MaximumScienceAttempts); Assert.Equal(41, destination.HorizonMinimumDegrees);
        Assert.Equal(SpectroscopyTargetFailurePolicy.SkipQualityFailure, destination.FailurePolicy);
        Assert.Empty(destination.CatalogId); Assert.Null(destination.CatalogMetadata);
        Assert.Equal(11, destination.Items.Count);
        Assert.NotSame(source.Target.InputCoordinates, destination.Target.InputCoordinates);
        Assert.NotSame(source.Target.InputCoordinates.Coordinates, destination.Target.InputCoordinates.Coordinates);
        source.Target.InputCoordinates.RAHours = 8;
        Assert.Equal(105, destination.RightAscensionDegrees);
        Assert.False(destination.TryImportDroppedTarget(new NINA.Sequencer.DragDrop.DropIntoParameters(new ObservationStageMarkerItem())));
        Assert.False(destination.TryImportDroppedTarget(null));

        using var lifetime = new CancellationTokenSource();
        typeof(UvexTargetObservationContainer).GetField("targetLifetime", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(destination, lifetime);
        Assert.False(destination.DropTargetCommand.CanExecute(drop));
        Assert.False(destination.TryImportDroppedTarget(drop));
        Assert.Equal(105, destination.RightAscensionDegrees);
        Assert.Throws<InvalidOperationException>(() => destination.AutomaticGuideRecovery = false);
        Assert.Throws<InvalidOperationException>(() => destination.GuideStarWaitSeconds = 180);
    }

    [Theory]
    [InlineData("normal")]
    [InlineData("active")]
    [InlineData("edited")]
    [InlineData("run-ended")]
    [InlineData("failure")]
    public async Task NativePlanetariumImportIsReadOnlyBoundedToTheSameIdleTarget(string mode)
    {
        InitializeAstrometry();
        var settings = Settings();
        var astrometry = Proxy<IAstrometrySettings>();
        var profile = Proxy<IProfile>((m, _) => m.Name == "get_AstrometrySettings" ? astrometry : Default(m.ReturnType));
        var profiles = Proxy<IProfileService>((m, _) => m.Name == "get_ActiveProfile" ? profile : Default(m.ReturnType));
        using var host = new ObservationCoordinatorHost(new SilentNotifier());
        var pending = new TaskCompletionSource<NINA.Astrometry.DeepSkyObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<string>();
        var planetarium = Proxy<NINA.Equipment.Interfaces.IPlanetarium>((m, _) =>
        {
            calls.Add(m.Name);
            return m.Name switch
            {
                "GetTarget" => pending.Task,
                "get_CanGetRotationAngle" => true,
                "GetRotationAngle" => Task.FromResult(32d),
                _ => throw new InvalidOperationException("Unexpected planning call: " + m.Name)
            };
        });
        var factory = Proxy<NINA.Equipment.Interfaces.IPlanetariumFactory>((m, _) =>
            m.Name == "GetPlanetarium" ? planetarium : throw new InvalidOperationException(m.Name));
        var original = new UvexTargetObservationContainer(profiles, Proxy<INighttimeCalculator>(), host, null!, settings,
            planetariumFactory: factory) { TargetName = "Original", RightAscensionDegrees = 90, DeclinationDegrees = 20,
                ScienceFrames = 3, FixedExposureSeconds = 300, ForceGuideCalibration = true };
        original.Target.Expanded = false;
        var target = (UvexTargetObservationContainer)original.Clone();
        Assert.False(target.Target.Expanded);
        Assert.True(target.CoordsFromPlanetariumCommand.CanExecute(null));
        var operation = target.ImportTargetFromPlanetariumAsync();
        Assert.True(target.TargetEditorBusy); Assert.False(target.CanEditTargetIdentity);
        Assert.False(await target.ImportTargetFromPlanetariumAsync()); // One outstanding metadata request.
        using var lifetime = new CancellationTokenSource();
        if (mode == "active") typeof(UvexTargetObservationContainer).GetField("targetLifetime", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, lifetime);
        if (mode == "edited") target.TargetName = "Operator edit";
        if (mode == "run-ended") target.RefreshPlanEditability(); // Invalidates a delayed read even after returning idle.
        var result = new NINA.Astrometry.DeepSkyObject("Imported",
            new NINA.Astrometry.Coordinates(105, -0.5, NINA.Astrometry.Epoch.J2000, NINA.Astrometry.Coordinates.RAType.Degrees), null!);
        if (mode == "failure") pending.SetException(new InvalidOperationException("Synthetic offline planetarium"));
        else pending.SetResult(result);
        Assert.Equal(mode == "normal", await operation);
        Assert.False(target.TargetEditorBusy);
        Assert.Equal(mode == "normal" ? "Imported" : mode == "edited" ? "Operator edit" : "Original", target.TargetName);
        Assert.Equal(mode == "normal" ? 105 : 90, target.RightAscensionDegrees);
        Assert.Equal(3, target.ScienceFrames); Assert.Equal(300, target.FixedExposureSeconds); Assert.True(target.ForceGuideCalibration);
        Assert.Equal("Original", original.TargetName); Assert.Equal(90, original.RightAscensionDegrees);
        Assert.All(calls, x => Assert.Contains(x, new[] { "GetTarget", "get_CanGetRotationAngle", "GetRotationAngle" }));
        Assert.Equal(1, calls.Count(x => x == "GetTarget"));
        if (mode == "normal")
        {
            Assert.Equal(32, target.Target.PositionAngle);
            result.Coordinates.RA = 10;
            Assert.Equal(105, target.RightAscensionDegrees);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeFramingExportOnlyCopiesCoordinatesAndDoesNotNavigateAfterRunStarts(bool runStarts)
    {
        InitializeAstrometry();
        var settings = Settings();
        var astrometry = Proxy<IAstrometrySettings>();
        var profile = Proxy<IProfile>((m, _) => m.Name == "get_AstrometrySettings" ? astrometry : Default(m.ReturnType));
        var profiles = Proxy<IProfileService>((m, _) => m.Name == "get_ActiveProfile" ? profile : Default(m.ReturnType));
        using var host = new ObservationCoordinatorHost(new SilentNotifier());
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        NINA.Astrometry.DeepSkyObject? captured = null;
        var framing = Proxy<NINA.WPF.Base.Interfaces.ViewModel.IFramingAssistantVM>((m, a) =>
        {
            Assert.Equal("SetCoordinates", m.Name);
            captured = Assert.IsType<NINA.Astrometry.DeepSkyObject>(a[0]);
            return pending.Task;
        });
        var tabs = new List<NINA.Core.Enum.ApplicationTab>();
        var application = Proxy<NINA.WPF.Base.Interfaces.Mediator.IApplicationMediator>((m, a) =>
        {
            Assert.Equal("ChangeTab", m.Name); tabs.Add((NINA.Core.Enum.ApplicationTab)a[0]!); return null;
        });
        var target = new UvexTargetObservationContainer(profiles, Proxy<INighttimeCalculator>(), host, null!, settings,
            framingAssistant: framing, applicationMediator: application)
            { TargetName = "Planning only", RightAscensionDegrees = 90, DeclinationDegrees = -0.5, ForceGuideCalibration = true };
        var operation = target.SendTargetToFramingAsync();
        Assert.True(target.TargetEditorBusy); Assert.NotNull(captured);
        Assert.NotSame(target.Target.InputCoordinates.Coordinates, captured.Coordinates);
        using var lifetime = new CancellationTokenSource();
        if (runStarts) typeof(UvexTargetObservationContainer).GetField("targetLifetime", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, lifetime);
        pending.SetResult(true);
        Assert.Equal(!runStarts, await operation);
        Assert.False(target.TargetEditorBusy);
        if (runStarts) Assert.Empty(tabs);
        else Assert.Equal(NINA.Core.Enum.ApplicationTab.FRAMINGASSISTANT, Assert.Single(tabs));
        Assert.Equal("Planning only", target.TargetName); Assert.True(target.ForceGuideCalibration);
        captured.Coordinates.RA = 10;
        Assert.Equal(90, target.RightAscensionDegrees);
    }

    [Fact]
    public void EditableTargetRoundTripsWithIndependentPlanAndNoStaleCatalogIdentity()
    {
        InitializeAstrometry();
        var settings = Settings();
        var originalFrameDefault = settings.AtrScienceFrameCount;
        var astrometry = Proxy<IAstrometrySettings>();
        var profile = Proxy<IProfile>((m, _) => m.Name == "get_AstrometrySettings" ? astrometry : Default(m.ReturnType));
        var profiles = Proxy<IProfileService>((m, _) => m.Name == "get_ActiveProfile" ? profile : Default(m.ReturnType));
        using var host = new ObservationCoordinatorHost(new SilentNotifier());
        UvexTargetObservationContainer Create() => new(profiles, Proxy<INighttimeCalculator>(), host, null!, settings);
        var first = Create();
        first.TargetName = "Original"; first.RightAscensionDegrees = 90; first.DeclinationDegrees = 20;
        first.Name = "01 · Original · 2×120s";
        first.ScienceFrames = 2; first.MaximumScienceAttempts = 4; first.FixedExposureSeconds = 120;
        first.CatalogId = "TEST-1";
        first.CatalogMetadata = new("Original", "TEST-1", 90, 20, "Synthetic catalog", "star", 8, DateTimeOffset.UtcNow);
        var metadata = first.CatalogMetadata;
        first.ForceGuideCalibration = true;
        first.AutomaticGuideRecovery = false;
        first.GuideStarWaitSeconds = 180;
        var clone = (UvexTargetObservationContainer)first.Clone();
        Assert.True(clone.ForceGuideCalibration);
        Assert.False(clone.AutomaticGuideRecovery);
        Assert.Equal(180, clone.GuideStarWaitSeconds);
        Assert.Equal(metadata, clone.CatalogMetadata);
        clone.TargetName = "Replacement"; clone.RightAscensionDegrees = 105;
        clone.ScienceFrames = 3; clone.FixedExposureSeconds = 300;
        Assert.Null(clone.CatalogMetadata); Assert.Empty(clone.CatalogId);
        Assert.Equal("Replacement · 3 × 300 s", clone.TargetSummary);
        Assert.Equal("01 · Replacement · 3 × 300 s", clone.Name);
        Assert.Equal("Original", first.TargetName); Assert.Equal(90, first.RightAscensionDegrees);
        Assert.Equal(metadata, first.CatalogMetadata); Assert.Equal(120, first.FixedExposureSeconds);
        Assert.Equal(originalFrameDefault, settings.AtrScienceFrameCount); // Shared defaults unchanged.
        var factory = Proxy<NINA.Sequencer.ISequencerFactory>((m, _) => m.Name switch
        {
            "GetContainer" => (UvexTargetObservationContainer)first.Clone(),
            "GetItem" => new ObservationStageMarkerItem(), _ => Default(m.ReturnType)
        });
        var converter = new NINA.Sequencer.Serialization.SequenceJsonConverter(factory);
        var restored = Assert.IsType<UvexTargetObservationContainer>(converter.Deserialize(converter.Serialize(first)));
        Assert.Equal(metadata, restored.CatalogMetadata);
        Assert.True(restored.ForceGuideCalibration);
        Assert.False(restored.AutomaticGuideRecovery);
        Assert.Equal(180, restored.GuideStarWaitSeconds);
        restored = Assert.IsType<UvexTargetObservationContainer>(converter.Deserialize(converter.Serialize(clone)));
        Assert.Equal(105, restored.RightAscensionDegrees); Assert.Equal(300, restored.FixedExposureSeconds);
        Assert.Equal(3, restored.ScienceFrames); Assert.True(restored.ForceGuideCalibration); Assert.Null(restored.CatalogMetadata);
        Assert.Equal(11, restored.Items.Count);
        var legacy = Newtonsoft.Json.Linq.JObject.Parse(converter.Serialize(first));
        legacy.Remove(nameof(first.ForceGuideCalibration));
        legacy.Remove(nameof(first.AutomaticGuideRecovery));
        legacy.Remove(nameof(first.GuideStarWaitSeconds));
        restored = Assert.IsType<UvexTargetObservationContainer>(converter.Deserialize(legacy.ToString()));
        Assert.False(restored.ForceGuideCalibration); // Even with a checked factory prototype.
        Assert.True(restored.AutomaticGuideRecovery);
        Assert.Equal(300, restored.GuideStarWaitSeconds);
        Assert.Equal(metadata, restored.CatalogMetadata);
        restored.DeclinationDegrees = 21;
        Assert.Null(restored.CatalogMetadata);
        Assert.Throws<ArgumentOutOfRangeException>(() => restored.RightAscensionDegrees = double.NaN);
        Assert.Throws<ArgumentOutOfRangeException>(() => restored.DeclinationDegrees = 91);
    }

    [Theory]
    [InlineData(true, false, true, false, true)]
    [InlineData(true, false, true, false, false)]
    [InlineData(true, false, false, true, true)]
    [InlineData(true, true, false, false, true)]
    [InlineData(true, true, false, true, true)]
    [InlineData(true, true, false, true, false)]
    [InlineData(false, false, false, false, true)]
    public void NightTerminalSeparatesPhysicalClosureFromUnsettledTargetResponsibility(
        bool real, bool cancelled, bool physicallyClosed, bool closeFailed, bool unsettled)
    {
        var boundaries = unsettled
            ? new[] { new NativeNightTargetBoundaryFailure("Synthetic target", "SIM-RUN",
                new[] { "运动回程责任未结清，禁止跳目标或声称收口成功。" }) }
            : Array.Empty<NativeNightTargetBoundaryFailure>();
        var text = UvexNightSequenceContainer.DescribeTerminalState(real, cancelled, physicallyClosed,
            closeFailed ? new[] { "Synthetic roof readback failure" } : Array.Empty<string>(), boundaries);
        Assert.Equal(unsettled, text.Contains("观测运动/交接责任未结清", StringComparison.Ordinal));
        Assert.DoesNotContain("整夜已收口", text);
        if (cancelled && closeFailed)
        {
            Assert.Contains("已请求取消；停止/空闲未确认，未执行机械收口", text);
            Assert.DoesNotContain("已取消；只停止采集", text);
        }
        else if (cancelled) Assert.Contains("不执行机械收口", text);
        else if (closeFailed) Assert.Contains("物理收口未完成", text);
        else if (!real) Assert.Contains("未操作硬件", text);
        else Assert.Contains("物理收口已确认", text);
    }

    [Theory]
    [InlineData("fault")]
    [InlineData("unsafe")]
    [InlineData("cancel")]
    public async Task NativeNightRetainsUnsettledTargetBoundaryWithoutContinuingOrReplacingOriginalCancellation(string mode)
    {
        InitializeAstrometry();
        var settings = Settings();
        var astrometry = Proxy<IAstrometrySettings>();
        var profile = Proxy<IProfile>((m, _) => m.Name == "get_AstrometrySettings" ? astrometry : Default(m.ReturnType));
        var profiles = Proxy<IProfileService>((m, _) => m.Name == "get_ActiveProfile" ? profile : Default(m.ReturnType));
        using var host = new ObservationCoordinatorHost(new SilentNotifier());
        var night = new UvexNightSequenceContainer(profiles, host, null!, settings) { StopAtDawn = false };
        const string unresolved = "运动回程责任未结清，禁止跳目标或声称收口成功。";
        var first = new UnsettledBoundaryRunner(() =>
        {
            // Replay the verified target-boundary result, not a new motion or
            // a fabricated successful return. The native terminal route owns it.
            night.RecordUnsettledTargetBoundary("Interrupted target", host.Dashboard.Run.ObservationRunId, new[] { unresolved });
            if (mode == "unsafe") night.OnNightUnsafe();
            if (mode == "cancel") _ = night.Interrupt();
        });
        var second = new TerminalGateRunner(null, false);
        for (var i = 0; i < 2; i++)
        {
            IObservationStageRunner runner = i == 0 ? first : second;
            night.Add(new UvexTargetObservationContainer(profiles, Proxy<INighttimeCalculator>(), host, null!, settings, () => runner)
            {
                TargetName = i == 0 ? "Interrupted target" : "Must not start", CatalogId = $"SIM-{i}", NightSetupId = "SIM-NIGHT",
                UseRealMode = false, SiteLatitudeDegrees = 33, SiteLongitudeDegreesEast = 120,
                RightAscensionDegrees = 0, DeclinationDegrees = 89, DurationMinutes = 1,
                HorizonMinimumDegrees = 0, HorizonStartMarginDegrees = 0, HorizonContinueMarginDegrees = 0,
                ExpectedAtrCameraId = "SIM-ATR", ExpectedG3ProfileName = "SIM-G3", ExpectedQhyCameraId = "SIM-QHY",
                ScienceFrames = 1, MaximumScienceAttempts = 2, FailurePolicy = SpectroscopyTargetFailurePolicy.SkipQualityFailure,
            });
        }
        var execution = night.Execute(new Progress<ApplicationStatus>(), CancellationToken.None);
        if (mode == "fault") await Assert.ThrowsAsync<InvalidOperationException>(() => execution.WaitAsync(TimeSpan.FromSeconds(10)));
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Empty(second.Stages);
        Assert.Contains("观测运动/交接责任未结清", night.NightState);
        using var journal = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(night.JournalPath));
        var terminal = journal.RootElement.GetProperty("outcomes").EnumerateArray().Last();
        Assert.False(terminal.GetProperty("physicalCloseoutConfirmed").GetBoolean());
        Assert.False(terminal.GetProperty("targetBoundariesSettled").GetBoolean());
        Assert.Equal(mode == "cancel", terminal.GetProperty("ordinaryCancel").GetBoolean());
        Assert.Equal(mode == "unsafe", terminal.GetProperty("unsafeTripped").GetBoolean());
        Assert.Empty(terminal.GetProperty("closeErrors").EnumerateArray());
        var boundary = Assert.Single(terminal.GetProperty("targetBoundaryFailures").EnumerateArray());
        Assert.Equal(host.Dashboard.Run.ObservationRunId, boundary.GetProperty("RunId").GetString());
        Assert.Equal(unresolved, Assert.Single(boundary.GetProperty("Errors").EnumerateArray()).GetString());
        Assert.Single(journal.RootElement.GetProperty("targetBoundaryFailures").EnumerateArray());
        Assert.False(night.IsActive);
        if (mode == "cancel")
        {
            var guard = Assert.IsType<NightSafetyStandby>(host.SafetyStandby);
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.ReserveNightAsync(new object(), false, CancellationToken.None));
            night.EndNightCommand.Execute(null);
            await guard.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        }
        using var reservation = await host.ReserveNightAsync(new object(), false, CancellationToken.None);
    }

    private sealed class UnsettledBoundaryRunner(Action recordBoundary) : ObservationStageRunnerBase
    {
        public override Task<StageResult> ExecuteStageAsync(ObservationStage stage, ObservationContext context, CancellationToken token)
        {
            if (stage != ObservationStage.AcquireG3SlitField)
                return Task.FromResult(new StageResult(GateResult.Pass("SIM_PASS", "Synthetic; no hardware.")));
            recordBoundary();
            token.ThrowIfCancellationRequested();
            return Task.FromResult(new StageResult(GateResult.Unknown("SIM_UNSETTLED_MOTION", "Synthetic interrupted motion responsibility.")));
        }
    }

    [Theory]
    [InlineData("explicit-close", true)]
    [InlineData("deadline", true)]
    [InlineData("unsafe", true)]
    [InlineData("cancel", true)]
    [InlineData("cancel", false)]
    public async Task FailedTargetHoldsOpenWithoutProceedingOrForgivingDebt(string exit, bool unsettled)
    {
        InitializeAstrometry();
        var settings = Settings();
        var profile = Proxy<IProfile>((m, _) => m.Name == "get_AstrometrySettings" ? Proxy<IAstrometrySettings>() : Default(m.ReturnType));
        var profiles = Proxy<IProfileService>((m, _) => m.Name == "get_ActiveProfile" ? profile : Default(m.ReturnType));
        using var host = new ObservationCoordinatorHost(new SilentNotifier());
        var night = new UvexNightSequenceContainer(profiles, host, null!, settings)
        {
            StopAtDawn = false, HoldOpenAfterTargets = false,
            EndAtIso8601 = DateTimeOffset.UtcNow.AddSeconds(exit == "deadline" ? 4 : 30).ToString("O"),
        };
        var later = new TerminalGateRunner(null, false);
        var failed = new UnsettledBoundaryRunner(() =>
        {
            if (unsettled) night.RecordUnsettledTargetBoundary("Failed", host.Dashboard.Run.ObservationRunId, new[] { "Unsettled simulated debt" });
        });
        for (var i = 0; i < 2; i++)
        {
            IObservationStageRunner runner = i == 0 ? failed : later;
            night.Add(new UvexTargetObservationContainer(profiles, Proxy<INighttimeCalculator>(), host, null!, settings, () => runner)
            {
                TargetName = i == 0 ? "Failed" : "Never start", CatalogId = $"SIM-{i}", NightSetupId = "SIM-NIGHT",
                UseRealMode = false, SiteLatitudeDegrees = 33, SiteLongitudeDegreesEast = 120,
                RightAscensionDegrees = 0, DeclinationDegrees = 89, DurationMinutes = 1,
                HorizonMinimumDegrees = 0, HorizonStartMarginDegrees = 0, HorizonContinueMarginDegrees = 0,
                ExpectedAtrCameraId = "SIM-ATR", ExpectedG3ProfileName = "SIM-G3", ExpectedQhyCameraId = "SIM-QHY",
                ScienceFrames = 1, MaximumScienceAttempts = 2, FailurePolicy = SpectroscopyTargetFailurePolicy.SkipQualityFailure,
            });
        }
        var closeout = new UvexObservatoryCloseoutItem { CloseAfterTargets = false };
        night.Add(closeout);
        using var cancel = new CancellationTokenSource();
        var task = night.Execute(new Progress<ApplicationStatus>(), cancel.Token);
        var error = await Record.ExceptionAsync(() => task.WaitAsync(TimeSpan.FromSeconds(8)));
        Assert.IsNotType<TimeoutException>(error);
        Assert.Contains("独立安全待机已接管", night.NightState);
        Assert.True(task.IsCompleted); Assert.False(night.IsActive); Assert.Empty(later.Stages);
        closeout.CloseAfterTargets = true; // Edits apply to the NEXT native execution only.
        var guard = Assert.IsType<NightSafetyStandby>(host.SafetyStandby);
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.RunFocusPreparationAsync(() => Task.FromResult(0), CancellationToken.None));
        if (exit == "explicit-close") night.EndNightCommand.Execute(null);
        if (exit == "unsafe") guard.RequestCloseout("NIGHT_UNSAFE");
        if (exit == "cancel")
        {
            cancel.Cancel(); await night.Interrupt();
            Assert.Equal("Waiting", guard.Snapshot.Phase); // A native cancel cannot disarm roof protection.
            night.EndNightCommand.Execute(null);
        }
        await guard.Completion.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Empty(later.Stages); Assert.False(night.IsActive);
        using var journal = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(night.JournalPath));
        var terminal = journal.RootElement.GetProperty("outcomes").EnumerateArray().Last();
        Assert.Equal(!unsettled, terminal.GetProperty("targetBoundariesSettled").GetBoolean());
        Assert.False(terminal.GetProperty("ordinaryCancel").GetBoolean());
        Assert.False(terminal.GetProperty("unsafeTripped").GetBoolean()); // Later events belong to the independent journal.
        Assert.True(terminal.GetProperty("handedToStandby").GetBoolean());
        Assert.False(terminal.GetProperty("physicalCloseoutConfirmed").GetBoolean()); // Simulation is never hardware proof.
    }

    [Fact]
    public async Task OptionalCloseoutHasExplicitScopeAndOverridesLegacyChoiceWithoutChangingProfile()
    {
        InitializeAstrometry();
        var settings = Settings();
        settings.CloseDomeOrRoofOnFinalize = false;
        settings.CloseDomeOrRoofOnFailure = false;
        var profile = Proxy<IProfile>((m, _) => m.Name == "get_AstrometrySettings" ? Proxy<IAstrometrySettings>() : Default(m.ReturnType));
        var profiles = Proxy<IProfileService>((m, _) => m.Name == "get_ActiveProfile" ? profile : Default(m.ReturnType));
        using var host = new ObservationCoordinatorHost(new SilentNotifier());
        var night = new UvexNightSequenceContainer(profiles, host, null!, settings) { StopAtDawn = false, EndAtIso8601 = "05:00", HoldOpenAfterTargets = true };
        night.Add(new UvexTargetObservationContainer(profiles, Proxy<INighttimeCalculator>(), host, null!, settings));
        var closeout = new UvexObservatoryCloseoutItem();
        Assert.False(closeout.Validate());
        await Assert.ThrowsAsync<InvalidOperationException>(() => closeout.Execute(new Progress<ApplicationStatus>(), CancellationToken.None));
        night.Add(closeout);
        Assert.True(closeout.Validate()); Assert.True(night.Validate());
        Assert.False(night.EffectiveHoldOpenAfterTargets);
        closeout.CloseAfterTargets = false;
        Assert.True(night.EffectiveHoldOpenAfterTargets);
        var clone = (UvexNightSequenceContainer)night.Clone();
        Assert.False(Assert.IsType<UvexObservatoryCloseoutItem>(clone.Items.Last()).CloseAfterTargets);
        Assert.Contains("\"CloseAfterTargets\":false", Newtonsoft.Json.JsonConvert.SerializeObject(clone,
            new Newtonsoft.Json.JsonSerializerSettings { PreserveReferencesHandling = Newtonsoft.Json.PreserveReferencesHandling.Objects }));
        night.EndAtIso8601 = "";
        Assert.False(night.Validate()); // No indefinite unprotected wait.
        night.EndAtIso8601 = "05:00";
        night.Add(new UvexObservatoryCloseoutItem());
        Assert.False(night.Validate()); // No competing terminal owners.
        Assert.False(closeout.Validate()); // No closeout before another child.
        Assert.False(settings.CloseDomeOrRoofOnFailure); Assert.False(settings.CloseDomeOrRoofOnFinalize);
    }

    [Fact]
    public void InitialSettleOverrideIsBoundedSavedAndDoesNotChangeProfileOrSlitTolerance()
    {
        var original = new NativeSequencePlan(2, 4, 120);
        var configured = new UvexAdv.Phd2.Phd2SettleCriteria(1.5, 10, 120);
        Assert.Same(configured, original.InitialGuideCriteria(configured));
        var plan = original with { InitialGuideSettlePixels = 3, InitialGuideStableSeconds = 5, InitialGuideTimeoutSeconds = 180 };
        Assert.Equal(new UvexAdv.Phd2.Phd2SettleCriteria(3, 5, 180), plan.InitialGuideCriteria(configured));
        Assert.Empty(plan.Validate());
        foreach (var bad in new[] { plan with { InitialGuideSettlePixels = 6 }, plan with { InitialGuideSettlePixels = double.NaN },
            plan with { InitialGuideStableSeconds = 0 }, plan with { InitialGuideTimeoutSeconds = 301 } })
            Assert.NotEmpty(bad.Validate());
        var settings = Settings();
        var solver = new PlateSolverRunConfiguration("primary", "blind", "primary-type", "blind-type",
            10, 10, 2, 500, true, 0.1, 5, 3, "", "", "", "", "", "", "", "");
        var before = RealRunConfiguration.Capture(settings, solver);
        var after = before.WithSequencePlan(plan);
        Assert.Equal(before.Phd2, after.Phd2);
        Assert.Equal(before.Slit, after.Slit);
        Assert.NotEqual(before.WithSequencePlan(original).ActionConfigurationSha256, after.ActionConfigurationSha256);
        Assert.True(after.MatchesCurrentProfile(settings, solver, out _));
    }

    [Theory]
    [InlineData("explicit-close")]
    [InlineData("deadline")]
    [InlineData("unsafe")]
    [InlineData("cancel")]
    public async Task TestStandbyKeepsNightOwnershipAndSafetyUntilSeparateCloseout(string mode)
    {
        InitializeAstrometry();
        var settings = Settings();
        var astrometry = Proxy<IAstrometrySettings>();
        var profile = Proxy<IProfile>((m, _) => m.Name == "get_AstrometrySettings" ? astrometry : Default(m.ReturnType));
        var profiles = Proxy<IProfileService>((m, _) => m.Name == "get_ActiveProfile" ? profile : Default(m.ReturnType));
        using var host = new ObservationCoordinatorHost(new SilentNotifier());
        var night = new UvexNightSequenceContainer(profiles, host, null!, settings)
        { StopAtDawn = false, HoldOpenAfterTargets = true, InitialGuideSettlePixels = 3 };
        night.Add(new UvexTargetObservationContainer(profiles, Proxy<INighttimeCalculator>(), host, null!, settings,
            () => new TerminalGateRunner(null, false))
        {
            TargetName = "Standby simulation", CatalogId = "SIM", NightSetupId = "SIM-NIGHT", UseRealMode = false,
            SiteLatitudeDegrees = 33, SiteLongitudeDegreesEast = 120, RightAscensionDegrees = 0, DeclinationDegrees = 89,
            DurationMinutes = 1, HorizonMinimumDegrees = 0, HorizonStartMarginDegrees = 0, HorizonContinueMarginDegrees = 0,
            ExpectedAtrCameraId = "SIM-ATR", ExpectedG3ProfileName = "SIM-G3", ExpectedQhyCameraId = "SIM-QHY",
            ScienceFrames = 1, MaximumScienceAttempts = 2,
        });
        Assert.False(night.Validate()); // An unbounded open-roof wait cannot be saved as executable.
        night.EndAtIso8601 = DateTimeOffset.UtcNow.AddSeconds(mode == "deadline" ? 4 : 30).ToString("O");
        var root = new SequenceRootContainer();
        root.Add(new StartAreaContainer());
        var targetArea = new TargetAreaContainer(); root.Add(targetArea); targetArea.Add(night);
        var endArea = new EndAreaContainer(); root.Add(endArea);
        endArea.Add(new UvexObservatoryCloseoutItem { CloseAfterTargets = false });
        var extras = new SequentialContainer(); endArea.Add(extras);
        extras.Add(new UvexNightClosedCondition());
        var endProbe = new NINA.Sequencer.SequenceItem.Utility.Annotation(); extras.Add(endProbe);
        var clone = (UvexNightSequenceContainer)night.Clone();
        Assert.True(clone.HoldOpenAfterTargets);
        Assert.Equal(3, clone.InitialGuideSettlePixels);
        var json = Newtonsoft.Json.JsonConvert.SerializeObject(night, new Newtonsoft.Json.JsonSerializerSettings
        { PreserveReferencesHandling = Newtonsoft.Json.PreserveReferencesHandling.All });
        Assert.Contains("\"HoldOpenAfterTargets\":true", json);
        using var cancel = new CancellationTokenSource();
        var execution = night.Execute(new Progress<ApplicationStatus>(), cancel.Token);
        await execution.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Contains("观测序列已结束", night.NightState);
        Assert.True(execution.IsCompleted);
        Assert.False(night.IsActive);
        var activeTarget = (UvexTargetObservationContainer)night.Items[0];
        Assert.True(night.CanEditPlan); Assert.True(activeTarget.CanEditPlan);
        var guard = Assert.IsType<NightSafetyStandby>(host.SafetyStandby);
        var originalDeadline = guard.Snapshot.DeadlineUtc;
        await endArea.Execute(new Progress<ApplicationStatus>(), CancellationToken.None);
        Assert.Equal(NINA.Core.Enum.SequenceEntityStatus.SKIPPED, endProbe.Status);
        Assert.False(night.NativeEndInstructionsReady);
        Assert.Same(guard, host.SafetyStandby);
        activeTarget.ForceGuideCalibration = true;
        activeTarget.TargetName = "Next run's target";
        activeTarget.FixedExposureSeconds = 600;
        // Edits to the now-idle form cannot change the detached safety request.
        night.HoldOpenAfterTargets = false; night.InitialGuideSettlePixels = 5; night.EndAtIso8601 = "";
        Assert.Equal(originalDeadline, guard.Snapshot.DeadlineUtc);
        Assert.Equal(3, night.ApplyNight(new(1, 1, 0)).InitialGuideSettlePixels);
        if (mode == "explicit-close") night.EndNightCommand.Execute(null);
        if (mode == "unsafe") guard.RequestCloseout("NIGHT_UNSAFE");
        if (mode == "cancel")
        {
            cancel.Cancel(); await night.Interrupt();
            Assert.Equal("Waiting", guard.Snapshot.Phase);
            night.EndNightCommand.Execute(null);
        }
        await guard.Completion.WaitAsync(TimeSpan.FromSeconds(8));
        extras.ResetAll();
        await extras.Execute(new Progress<ApplicationStatus>(), CancellationToken.None);
        Assert.Equal(NINA.Core.Enum.SequenceEntityStatus.SKIPPED, endProbe.Status);
        Assert.False(night.IsActive);
        using var journal = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(night.JournalPath));
        var terminal = journal.RootElement.GetProperty("outcomes").EnumerateArray().Last();
        Assert.False(terminal.GetProperty("ordinaryCancel").GetBoolean());
        Assert.False(terminal.GetProperty("unsafeTripped").GetBoolean());
        Assert.True(terminal.GetProperty("handedToStandby").GetBoolean());
        Assert.True(journal.RootElement.GetProperty("holdOpenAfterTargets").GetBoolean());
        Assert.False(string.IsNullOrEmpty(journal.RootElement.GetProperty("deadlineSchedule").GetProperty("RequestedText").GetString()));
        using var released = await host.ReserveNightAsync(new object(), false, CancellationToken.None);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void HorizonPreflightSkipRequiresExplicitPolicyAndValidPlan(bool skip, bool invalid, bool expected)
    {
        var plan = ObservationPlanFactory.Create("Below horizon", "SIM", 0, -89, 35, "SIM-NIGHT",
            33, 120, 0, 42, 0, 0, invalid ? "" : "SIM-ATR", "SIM-G3", "SIM-QHY",
            new MotionLimits(0.01, 0.1, 3, TimeSpan.FromMinutes(5)), true);
        var result = NativeSequencePlan.PreflightHorizonSkip(plan, skip
            ? SpectroscopyTargetFailurePolicy.SkipQualityFailure : SpectroscopyTargetFailurePolicy.EndNight);
        Assert.Equal(expected, result is not null);
        if (result is not null) Assert.Equal("HORIZON_BLOCKED", result.Code);
        Assert.False(NativeSequencePlan.IsSkippableQualityFailure("HORIZON_BLOCKED"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task HorizonPreflightSkipsWithoutConstructingRunnerAndContinuesNativeTargets(int skippedIndex)
    {
        InitializeAstrometry();
        var settings = Settings();
        var astrometry = Proxy<IAstrometrySettings>();
        var profile = Proxy<IProfile>((m, _) => m.Name == "get_AstrometrySettings" ? astrometry : Default(m.ReturnType));
        var profiles = Proxy<IProfileService>((m, _) => m.Name == "get_ActiveProfile" ? profile : Default(m.ReturnType));
        var notifier = new SilentNotifier();
        using var host = new ObservationCoordinatorHost(notifier);
        var night = new UvexNightSequenceContainer(profiles, host, null!, settings) { StopAtDawn = false };
        var jd = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 86400000d + 2440587.5;
        var ra = ((280.46061837 + 360.98564736629 * (jd - 2451545) + 120) % 360 + 360) % 360;
        var created = new List<int>();
        for (var i = 0; i < 3; i++)
        {
            var index = i;
            night.Add(new UvexTargetObservationContainer(profiles, Proxy<INighttimeCalculator>(), host, null!, settings,
                () => { created.Add(index); return new TerminalGateRunner(null, false); })
            {
                TargetName = $"Horizon boundary {i}", CatalogId = $"SIM-{i}", NightSetupId = "SIM-NIGHT",
                UseRealMode = false, SiteLatitudeDegrees = 33, SiteLongitudeDegreesEast = 120,
                RightAscensionDegrees = ra, DeclinationDegrees = i == skippedIndex ? -89 : 33, DurationMinutes = 1,
                HorizonMinimumDegrees = 42, HorizonStartMarginDegrees = 0, HorizonContinueMarginDegrees = 0,
                ExpectedAtrCameraId = "SIM-ATR", ExpectedG3ProfileName = "SIM-G3", ExpectedQhyCameraId = "SIM-QHY",
                ScienceFrames = 1, MaximumScienceAttempts = 2, FailurePolicy = SpectroscopyTargetFailurePolicy.SkipQualityFailure,
            });
        }
        await night.Execute(new Progress<ApplicationStatus>(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.DoesNotContain(skippedIndex, created);
        Assert.Equal(2, created.Count);
        Assert.Equal(2, notifier.Notifications.Count(x => x.Severity == ObservationAttentionSeverity.Success));
        Assert.Contains("未观测", ((UvexTargetObservationContainer)night.Items[skippedIndex]).LastOutcome);
        Assert.Equal("完成", ((UvexTargetObservationContainer)night.Items[2]).LastOutcome);
        using var journal = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(night.JournalPath));
        var outcomes = journal.RootElement.GetProperty("outcomes").EnumerateArray()
            .Where(x => x.TryGetProperty("outcome", out _)).ToArray();
        Assert.Equal(3, outcomes.Length);
        Assert.Equal("SkippedPreflightHorizon", outcomes[skippedIndex].GetProperty("outcome").GetString());
        Assert.True(outcomes[skippedIndex].GetProperty("noTargetRunnerCreated").GetBoolean());
        Assert.Equal("HORIZON_BLOCKED", outcomes[skippedIndex].GetProperty("gate").GetString());
        Assert.Equal(42, outcomes[skippedIndex].GetProperty("evaluatedPlan").GetProperty("Horizon")
            .GetProperty("BaseMinimumAltitudeDegrees").GetDouble());
    }

    [Fact]
    public void NightHomeIsExplicitBoundedAndPartOfImmutablePlan()
    {
        var original = new NativeSequencePlan(2, 4, 300);
        Assert.False(original.HomeBeforeParkOnFinalize);
        Assert.Equal(180, original.MountHomeTimeoutSeconds);
        Assert.NotEmpty((original with { MountHomeTimeoutSeconds = 0 }).Validate());
        Assert.NotEmpty((original with { MountHomeTimeoutSeconds = 601 }).Validate());
        var settings = Settings();
        var astrometry = Proxy<IAstrometrySettings>();
        var profile = Proxy<IProfile>((m, _) => m.Name == "get_AstrometrySettings" ? astrometry : Default(m.ReturnType));
        var profiles = Proxy<IProfileService>((m, _) => m.Name == "get_ActiveProfile" ? profile : Default(m.ReturnType));
        using var host = new ObservationCoordinatorHost(new SilentNotifier());
        var night = new UvexNightSequenceContainer(profiles, host, null!, settings)
        { HomeBeforeParkOnFinalize = true, StopAtDawn = false };
        night.Add(new UvexTargetObservationContainer(profiles, Proxy<INighttimeCalculator>(), host, null!, settings)
        { UseRealMode = true });
        Assert.False(night.Validate());
        Assert.Contains(night.Issues, x => x.Contains("Home → Park", StringComparison.Ordinal));
    }

    [Fact]
    public void RoofModeIsDefaultOffCapturedAndInheritedByEveryNightTarget()
    {
        var settings = Settings();
        var solver = new PlateSolverRunConfiguration("primary", "blind", "primary-type", "blind-type",
            10, 10, 2, 500, true, 0.1, 5, 3, "", "", "", "", "", "", "", "");
        var original = RealRunConfiguration.Capture(settings, solver);
        Assert.False(original.Environment.RoofNeverInterferesWithMount);
        settings.RoofNeverInterferesWithMount = true;
        var configured = RealRunConfiguration.Capture(settings, solver);
        Assert.NotEqual(original.ActionConfigurationSha256, configured.ActionConfigurationSha256);
        Assert.False(original.MatchesCurrentProfile(settings, solver, out _));
        var target = configured.WithSequencePlan(new(2, 4, 300, true));
        Assert.True(target.Environment.RoofNeverInterferesWithMount);
        Assert.True(target.MatchesCurrentProfile(settings, solver, out _));
        settings.RoofNeverInterferesWithMount = false;
        Assert.False(target.MatchesCurrentProfile(settings, solver, out _));
        Assert.True(target.Environment.RoofNeverInterferesWithMount);
    }

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
        var automatic = original.WithSequencePlan(new(2, 4, 0, true));
        Assert.Equal(original.Atr.ExposureLadderSeconds, automatic.Atr.ExposureLadderSeconds);
        Assert.Equal(original.Atr.ProbeExposureSeconds, automatic.Atr.ProbeExposureSeconds);
        Assert.NotEqual(first.ActionConfigurationSha256, automatic.ActionConfigurationSha256);
        Assert.True(automatic.MatchesCurrentProfile(settings, solver, out _));
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
    [InlineData(G3WcsRecoveryPolicy.ExhaustedReturnedCode, true)]
    [InlineData(G3WcsRecoveryPolicy.ResponseNotConvergedCode, true)]
    [InlineData("G3_WCS_CENTERING_RETURN_BLOCKED", false)]
    [InlineData("G3_MOTION_RETURN_TIME_LIMIT", false)]
    [InlineData("UNCLASSIFIED_RETURNED", false)]
    [InlineData("G3_POST_WCS_SHORT_UNCONFIRMED", true)]
    [InlineData(G3PostWcsShortHandoffPolicy.OutsideCode, true)]
    [InlineData(G3PostWcsShortHandoffPolicy.RecheckCode, false)]
    [InlineData("G3_MOTION_TERMINAL_HANDOFF_INCONSISTENT", false)]
    [InlineData("G3_MOTION_CRASH_RETURN_BLOCKED", false)]
    [InlineData("PHD2_GUIDING_FRAME_TIMEOUT", false)]
    [InlineData("PHD2_LOCK_RETURN_POSITION_UNCONFIRMED", true)]
    [InlineData("PHD2_STOP_UNCONFIRMED", false)]
    [InlineData(HorizonEvaluation.WindowExhaustedCode, true)]
    [InlineData("HORIZON_BLOCKED", false)]
    [InlineData("COMMAND_COORDINATE_HORIZON_BLOCKED", false)]
    [InlineData("STAGE_EXCEPTION", false)]
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
    [InlineData("clock-deadline")]
    [InlineData("cancel")]
    [InlineData("target-interrupt")]
    [InlineData("noninterfering-roof")]
    public async Task TwoTargetsRunThroughNativeSequentialStrategyAndSharedCoordinatorWithoutHardware(string mode)
    {
        InitializeAstrometry();
        var settings = Settings();
        var astrometry = Proxy<IAstrometrySettings>();
        settings.RoofNeverInterferesWithMount = mode == "noninterfering-roof";
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
        var night = new UvexNightSequenceContainer(profiles, host, null!, settings)
        { StopAtDawn = false, HomeBeforeParkOnFinalize = true, MountHomeTimeoutSeconds = 120,
            HoldOpenAfterTargets = true,
            InitialGuideSettlePixels = 3, InitialGuideStableSeconds = 5, InitialGuideTimeoutSeconds = 180 };
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
        Assert.True(clone.HomeBeforeParkOnFinalize);
        Assert.True(clone.HoldOpenAfterTargets);
        Assert.Equal(120, clone.MountHomeTimeoutSeconds);
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
        Assert.True(night.HoldOpenAfterTargets);
        night = Assert.IsType<UvexNightSequenceContainer>(converter.Deserialize(converter.Serialize(night)));
        Assert.True(night.HoldOpenAfterTargets);
        // Exercise both persisted values through the actual native converter;
        // this execution test does not enter the separately tested standby mode.
        night.HoldOpenAfterTargets = false;
        night = Assert.IsType<UvexNightSequenceContainer>(converter.Deserialize(converter.Serialize(night)));
        Assert.False(night.HoldOpenAfterTargets);
        Assert.True(night.HomeBeforeParkOnFinalize);
        Assert.Equal(120, night.MountHomeTimeoutSeconds);
        Assert.Equal(3, night.InitialGuideSettlePixels);
        Assert.Equal(5, night.InitialGuideStableSeconds);
        Assert.Equal(180, night.InitialGuideTimeoutSeconds);
        Assert.Equal(2, night.Items.Count);
        Assert.All(night.Items.Cast<UvexTargetObservationContainer>(), x => Assert.Equal(11, x.Items.Count));
        Assert.Equal(2, ((UvexTargetObservationContainer)night.Items[1]).ScienceFrames);
        if (mode == "deadline") night.EndAtIso8601 = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O");
        if (mode == "clock-deadline") night.EndAtIso8601 = "12:00"; // The current observing night's start is always elapsed.
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
        if (mode is not ("complete" or "noninterfering-roof"))
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

    [Theory]
    [InlineData("skip", "G3_CATALOG_SHORT_POSITION_UNCONFIRMED")]
    [InlineData("skip", "G3_SEARCH_DURABLE_RESERVE_INVALID")]
    [InlineData("cleanup-failure", "G3_SEARCH_DURABLE_RESERVE_INVALID")]
    [InlineData("end-policy", "G3_SEARCH_DURABLE_RESERVE_INVALID")]
    [InlineData("skip", "G3_SEARCH_NOT_STARTED_RETURNED")]
    [InlineData("cleanup-failure", "G3_SEARCH_NOT_STARTED_RETURNED")]
    [InlineData("end-policy", "G3_SEARCH_NOT_STARTED_RETURNED")]
    [InlineData("unknown-gate", "G3_SEARCH_RETURN_BLOCKED")]
    [InlineData("unknown-gate", "G3_MOTION_FAMILY_RADIUS_INCOMPATIBLE")]
    [InlineData("skip", G3WcsRecoveryPolicy.ExhaustedReturnedCode)]
    [InlineData("skip", G3WcsRecoveryPolicy.ResponseNotConvergedCode)]
    [InlineData("cleanup-failure", G3WcsRecoveryPolicy.ExhaustedReturnedCode)]
    [InlineData("cleanup-failure", G3WcsRecoveryPolicy.ResponseNotConvergedCode)]
    [InlineData("end-policy", G3WcsRecoveryPolicy.ExhaustedReturnedCode)]
    [InlineData("end-policy", G3WcsRecoveryPolicy.ResponseNotConvergedCode)]
    [InlineData("unknown-gate", "G3_WCS_CENTERING_RETURN_BLOCKED")]
    [InlineData("unknown-gate", "G3_MOTION_RETURN_TIME_LIMIT")]
    [InlineData("unknown-gate", "UNCLASSIFIED_RETURNED")]
    [InlineData("skip", "G3_POST_WCS_SHORT_UNCONFIRMED")]
    [InlineData("skip", "G3_POST_WCS_TARGET_NOT_MEASURED")]
    [InlineData("cleanup-failure", "G3_POST_WCS_TARGET_NOT_MEASURED")]
    [InlineData("end-policy", "G3_POST_WCS_TARGET_NOT_MEASURED")]
    [InlineData("fresh-window-skip", "G3_POST_WCS_TARGET_NOT_MEASURED")]
    [InlineData("fresh-window-cleanup-failure", "G3_POST_WCS_TARGET_NOT_MEASURED")]
    [InlineData("fresh-window-end-policy", "G3_POST_WCS_TARGET_NOT_MEASURED")]
    [InlineData("skip", G3PostWcsShortHandoffPolicy.OutsideCode)]
    [InlineData("skip", "PHD2_INITIAL_SETTLE_NOT_REACHED")]
    [InlineData("skip", "PHD2_LOCK_RETURN_POSITION_UNCONFIRMED")]
    [InlineData("cleanup-failure", "PHD2_LOCK_RETURN_POSITION_UNCONFIRMED")]
    [InlineData("end-policy", "PHD2_LOCK_RETURN_POSITION_UNCONFIRMED")]
    [InlineData("unknown-gate", "PHD2_STOP_UNCONFIRMED")]
    [InlineData("unknown-gate", "MOUNT_CLOCK_RECHECK_REQUIRED")]
    [InlineData("skip", Phd2PlacementGuideWindowPolicy.LegacyPreMotionRejectedCode)]
    [InlineData("cleanup-failure", Phd2PlacementGuideWindowPolicy.LegacyPreMotionRejectedCode)]
    [InlineData("end-policy", Phd2PlacementGuideWindowPolicy.LegacyPreMotionRejectedCode)]
    [InlineData("unknown-gate", "PHD2_GUIDE_WINDOW_NOT_STABLE")]
    [InlineData("cleanup-failure", "PHD2_INITIAL_SETTLE_NOT_REACHED")]
    [InlineData("end-policy", "PHD2_INITIAL_SETTLE_NOT_REACHED")]
    [InlineData("unknown-gate", "PHD2_SLIT_PLACEMENT_FAILED_SAFE")]
    [InlineData("end-policy", "G3_CATALOG_SHORT_POSITION_UNCONFIRMED")]
    [InlineData("end-policy", "G3_POST_WCS_SHORT_UNCONFIRMED")]
    [InlineData("unknown-gate", "UNCLASSIFIED_TEST_FAULT")]
    [InlineData("skip", AdaptiveGuidingRecovery.ExhaustedCode)]
    [InlineData("skip", AdaptiveGuidingRecovery.StarsTimeoutCode)]
    [InlineData("end-policy", AdaptiveGuidingRecovery.ExhaustedCode)]
    [InlineData("cleanup-failure", AdaptiveGuidingRecovery.ExhaustedCode)]
    [InlineData("cleanup-failure", "G3_CATALOG_SHORT_POSITION_UNCONFIRMED")]
    [InlineData("cleanup-failure", "G3_POST_WCS_SHORT_UNCONFIRMED")]
    [InlineData("typed-skip", "PHD2_FRESH_GUIDE_WINDOW_DEADLINE")]
    [InlineData("typed-skip", "PHD2_PRE_GUIDE_SELECTION_UNAVAILABLE")]
    [InlineData("typed-cleanup-failure", "PHD2_PRE_GUIDE_SELECTION_UNAVAILABLE")]
    [InlineData("typed-end-policy", "PHD2_PRE_GUIDE_SELECTION_UNAVAILABLE")]
    [InlineData("unknown-gate", "PHD2_PRE_GUIDE_OWNER_CHANGED")]
    [InlineData("typed-skip", G3WcsRecoveryPolicy.PierChangedCode)]
    [InlineData("typed-cleanup-failure", G3WcsRecoveryPolicy.PierChangedCode)]
    [InlineData("typed-end-policy", G3WcsRecoveryPolicy.PierChangedCode)]
    [InlineData("unknown-gate", G3WcsRecoveryPolicy.PierChangedCode)]
    [InlineData("typed-skip", "PHD2_FRESH_SLIT_REACQUISITION_EXHAUSTED")]
    [InlineData("typed-skip", "PHD2_SCIENCE_FRESH_SLIT_WINDOW_REJECTED")]
    [InlineData("typed-cleanup-failure", "PHD2_FRESH_GUIDE_WINDOW_DEADLINE")]
    [InlineData("typed-end-policy", "PHD2_FRESH_GUIDE_WINDOW_DEADLINE")]
    [InlineData("unknown-gate", "PHD2_FRESH_GUIDE_WINDOW_DEADLINE")]
    public async Task NativeFailedFirstTargetJoinsCoordinatorBeforeDecidingWhetherToSkip(string mode, string failureCode)
        => await AssertNativeFailureBoundaryAsync(mode, failureCode);

    [Theory]
    [InlineData("typed-skip")]
    [InlineData("typed-cleanup-failure")]
    [InlineData("typed-end-policy")]
    [InlineData("unknown-gate")]
    [InlineData("late-cancel")]
    [InlineData("late-unsafe")]
    [InlineData("late-deadline")]
    [InlineData("late-dawn")]
    public async Task NativeEndGroupHonorsTheActualJoinedTargetAndSafetyBoundary(string mode) =>
        await AssertNativeFailureBoundaryAsync(mode, "AUDITED_TARGET_FAILURE", nativeEndArea: true);

    internal async Task AssertNativeFailureBoundaryAsync(string mode, string failureCode,
        GateResult? producedGate = null, ObservationStage failureStage = ObservationStage.AcquireG3SlitField,
        bool nativeEndArea = false)
    {
        var typedQuality = mode.StartsWith("typed-", StringComparison.Ordinal);
        if (typedQuality) mode = mode[6..];
        var freshWindow = mode.StartsWith("fresh-window-", StringComparison.Ordinal);
        if (freshWindow) mode = mode[13..];
        InitializeAstrometry();
        var settings = Settings();
        var astrometry = Proxy<IAstrometrySettings>();
        var profile = Proxy<IProfile>((m, _) => m.Name == "get_AstrometrySettings" ? astrometry : Default(m.ReturnType));
        var profiles = Proxy<IProfileService>((m, _) => m.Name == "get_ActiveProfile" ? profile : Default(m.ReturnType));
        var notifier = new SilentNotifier();
        using var host = new ObservationCoordinatorHost(notifier);
        // Faulted releases the native marker while persistence is still joining.
        // Quality notifications now wait for the saved native boundary, so make
        // the original race repeatable from the snapshot event, not the toast.
        var delayed = 0;
        var night = new UvexNightSequenceContainer(profiles, host, null!, settings) { StopAtDawn = false };
        using var cancel = new CancellationTokenSource();
        host.DashboardChanged += (_, d) => {
            if (d.Run.State != ObservationRunState.Faulted || Interlocked.Exchange(ref delayed, 1) != 0) return;
            if (mode == "stale-gate") host.PublishGate(ObservationStage.FinalizeObservation, GateResult.Unknown("STALE_SIBLING_FAILURE", "Not the terminal stage."));
            if (mode == "late-cancel") cancel.Cancel();
            if (mode == "late-unsafe") night.OnNightUnsafe();
            if (mode == "late-end") night.StopAtNativeConditionBoundary();
            if (mode is "late-deadline" or "late-dawn")
            {
                // Inject the night condition exactly during the terminal join.
                // Use the production StopGateNow path, not a fake skip result.
                var field = typeof(UvexNightSequenceContainer).GetField("standbyPlan", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var plan = (NativeSequencePlan)field.GetValue(night)!;
                if (mode == "late-deadline") plan = plan with { DeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(-1) };
                else
                {
                    var now = DateTime.UtcNow;
                    var longitude = NINA.Astrometry.AstroUtil.GetSunAltitude(now, new NINA.Astrometry.ObserverInfo { Latitude = 0, Longitude = 0 }) >= -12 ? 0 : 180;
                    plan = plan with { StopAtDawn = true, DawnSunAltitudeDegrees = -12, LatitudeDegrees = 0, LongitudeDegrees = longitude };
                }
                Assert.Equal(mode == "late-deadline" ? "NIGHT_DEADLINE_REACHED" : "NIGHT_DAWN_REACHED", plan.StopGateNow()!.Code);
                field.SetValue(night, plan);
            }
            Thread.Sleep(500);
        };
        var first = new TerminalGateRunner(failureCode, mode == "cleanup-failure", typedQuality, freshWindow, producedGate, failureStage);
        var second = new TerminalGateRunner(null, false);
        var jd = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 86400000d + 2440587.5;
        var ra = ((280.46061837 + 360.98564736629 * (jd - 2451545) + 120) % 360 + 360) % 360;
        foreach (var (runner, index) in new[] { (first, 0), (second, 1) })
            night.Add(new UvexTargetObservationContainer(profiles, Proxy<INighttimeCalculator>(), host, null!, settings, () => runner)
            {
                TargetName = $"Quality boundary {index}", CatalogId = $"SIM-{index}", NightSetupId = "SIM-NIGHT",
                UseRealMode = false, SiteLatitudeDegrees = 33, SiteLongitudeDegreesEast = 120,
                RightAscensionDegrees = ra, DeclinationDegrees = 33, DurationMinutes = 1,
                HorizonMinimumDegrees = 0, HorizonStartMarginDegrees = 0, HorizonContinueMarginDegrees = 0,
                ExpectedAtrCameraId = "SIM-ATR", ExpectedG3ProfileName = "SIM-G3", ExpectedQhyCameraId = "SIM-QHY",
                ScienceFrames = 1, MaximumScienceAttempts = 2,
                FailurePolicy = mode == "end-policy" ? SpectroscopyTargetFailurePolicy.EndNight : SpectroscopyTargetFailurePolicy.SkipQualityFailure,
            });
        NINA.Sequencer.Container.EndAreaContainer? endArea = null;
        NINA.Sequencer.SequenceItem.Utility.Annotation? endProbe = null;
        if (nativeEndArea)
        {
            var root = new SequenceRootContainer();
            root.Add(new StartAreaContainer());
            var area = new TargetAreaContainer(); root.Add(area); area.Add(night);
            endArea = new EndAreaContainer(); root.Add(endArea);
            endArea.Add(new UvexObservatoryCloseoutItem { CloseAfterTargets = true });
            var group = new SequentialContainer(); endArea.Add(group);
            var condition = new UvexNightClosedCondition(); group.Conditions.Add(condition); condition.AttachNewParent(group);
            endProbe = new NINA.Sequencer.SequenceItem.Utility.Annotation { Text = "Native end probe; no equipment." };
            group.Add(endProbe);
        }
        var error = await Record.ExceptionAsync(() => night.Execute(new Progress<ApplicationStatus>(), cancel.Token).WaitAsync(TimeSpan.FromSeconds(15)));
        if (mode is not ("late-cancel" or "late-unsafe")) Assert.Null(error);
        using var journal = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(night.JournalPath));
        var outcomes = journal.RootElement.GetProperty("outcomes").EnumerateArray()
            .Where(x => x.TryGetProperty("outcome", out _)).ToArray();
        var skip = mode is "skip" or "stale-gate";
        if (endArea is not null)
        {
            // Even explicitly entering End with a fresh token cannot revive an unsafe/cancelled night.
            await endArea.Execute(new Progress<ApplicationStatus>(), CancellationToken.None);
            Assert.Equal(skip, endProbe!.Status == NINA.Core.Enum.SequenceEntityStatus.FINISHED);
            Assert.Equal(skip, night.NativeEndInstructionsReady);
            Assert.False(((UvexNightSequenceContainer)night.Clone()).NativeEndInstructionsReady);
            night.ResetProgress();
            Assert.False(night.NativeEndInstructionsReady);
        }
        Assert.Equal(skip ? 2 : 1, outcomes.Length);
        Assert.Equal(skip ? "SkippedQualityFailure" : "Stopped", outcomes[0].GetProperty("outcome").GetString());
        Assert.Equal(failureCode, outcomes[0].GetProperty("gate").GetString());
        var boundaryNotification = Assert.Single(notifier.Notifications, x => x.Fingerprint.Contains("|NativeBoundary|"));
        Assert.Equal(skip ? ObservationAttentionSeverity.Warning : ObservationAttentionSeverity.Error, boundaryNotification.Severity);
        Assert.Contains("Quality boundary 0", boundaryNotification.Body);
        Assert.Contains(failureCode, boundaryNotification.Body);
        if (skip) Assert.DoesNotContain(notifier.Notifications, x => x.Severity == ObservationAttentionSeverity.Error);
        Assert.Equal(mode == "late-end", outcomes[0].GetProperty("endRequested").GetBoolean());
        Assert.Equal(skip, second.Stages.Contains(ObservationStage.FinalizeObservation));
        Assert.DoesNotContain(failureStage switch
        {
            ObservationStage.RunScienceBlock => ObservationStage.FinalizeObservation,
            ObservationStage.SelectAtrExposure => ObservationStage.RunScienceBlock,
            _ => ObservationStage.SelectAtrExposure,
        }, first.Stages);
        if (skip)
        {
            Assert.Equal(ObservationRunState.Completed, host.Dashboard.Run.State);
            Assert.Equal("Completed", outcomes[1].GetProperty("outcome").GetString());
            Assert.Contains("跳过", ((UvexTargetObservationContainer)night.Items[0]).LastOutcome);
            Assert.Equal("完成", ((UvexTargetObservationContainer)night.Items[1]).LastOutcome);
        }
        else Assert.NotEqual("完成", ((UvexTargetObservationContainer)night.Items[1]).LastOutcome);
        if (host.SafetyStandby is { } standby)
        {
            night.EndNightCommand.Execute(null);
            await standby.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    [Theory]
    [InlineData(true, false, false)] [InlineData(false, false, false)]
    [InlineData(true, true, false)] [InlineData(false, true, false)]
    [InlineData(true, false, true)] [InlineData(false, false, true)]
    public async Task NativeNightPreservesCheckpointWindowGateAndOnlySkipsAfterConfirmedStop(
        bool insideStage, bool failCleanup, bool endPolicy)
    {
        InitializeAstrometry();
        var settings = Settings();
        var astrometry = Proxy<IAstrometrySettings>();
        var profile = Proxy<IProfile>((m, _) => m.Name == "get_AstrometrySettings" ? astrometry : Default(m.ReturnType));
        var profiles = Proxy<IProfileService>((m, _) => m.Name == "get_ActiveProfile" ? profile : Default(m.ReturnType));
        using var host = new ObservationCoordinatorHost(new SilentNotifier());
        var night = new UvexNightSequenceContainer(profiles, host, null!, settings) { StopAtDawn = false };
        var jd = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 86400000d + 2440587.5;
        var ra = ((280.46061837 + 360.98564736629 * (jd - 2451545) + 120) % 360 + 360) % 360;
        var first = new WindowCheckpointRunner(insideStage, failCleanup);
        var second = new TerminalGateRunner(null, false);
        for (var i = 0; i < 2; i++)
        {
            IObservationStageRunner runner = i == 0 ? first : second;
            night.Add(new UvexTargetObservationContainer(profiles, Proxy<INighttimeCalculator>(), host, null!, settings, () => runner)
            {
                TargetName = $"Window boundary {i}", CatalogId = $"SIM-{i}", NightSetupId = "SIM-NIGHT",
                UseRealMode = false, SiteLatitudeDegrees = 33, SiteLongitudeDegreesEast = 120,
                RightAscensionDegrees = ra, DeclinationDegrees = 33, DurationMinutes = 1,
                HorizonMinimumDegrees = 42, HorizonStartMarginDegrees = 0, HorizonContinueMarginDegrees = 0,
                ExpectedAtrCameraId = "SIM-ATR", ExpectedG3ProfileName = "SIM-G3", ExpectedQhyCameraId = "SIM-QHY",
                ScienceFrames = 1, MaximumScienceAttempts = 2,
                FailurePolicy = endPolicy ? SpectroscopyTargetFailurePolicy.EndNight : SpectroscopyTargetFailurePolicy.SkipQualityFailure,
            });
        }
        await night.Execute(new Progress<ApplicationStatus>(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
        var skip = !failCleanup && !endPolicy;
        Assert.False(first.ActionAfterCheckpoint);
        Assert.True(first.StopCalls > 0);
        Assert.Equal(skip, second.Stages.Contains(ObservationStage.FinalizeObservation));
        using var journal = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(night.JournalPath));
        var outcomes = journal.RootElement.GetProperty("outcomes").EnumerateArray()
            .Where(x => x.TryGetProperty("outcome", out _)).ToArray();
        Assert.Equal(skip ? "SkippedQualityFailure" : "Stopped", outcomes[0].GetProperty("outcome").GetString());
        Assert.Equal(HorizonEvaluation.WindowExhaustedCode, outcomes[0].GetProperty("gate").GetString());
        using var manifest = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(outcomes[0].GetProperty("ManifestPath").GetString()!));
        var failed = manifest.RootElement.GetProperty("gates").EnumerateObject()
            .Select(x => x.Value).Single(x => x.GetProperty("code").GetString() == HorizonEvaluation.WindowExhaustedCode);
        Assert.True(failed.GetProperty("metrics").GetProperty("currentAltitudeDegrees").GetDouble() > 42);
        Assert.Equal(720, failed.GetProperty("metrics").GetProperty("protectedDurationMinutes").GetDouble());
        Assert.DoesNotContain("STAGE_EXCEPTION", manifest.RootElement.GetRawText());
        if (skip) Assert.Equal("Completed", outcomes[1].GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task CheckpointPersistenceFailureCannotSuppressOwnerStopOrPublishSkippableGate()
    {
        using var host = new ObservationCoordinatorHost(new SilentNotifier());
        // Deliberately omit a durable run session to exercise the persistence
        // veto without touching hardware or weakening the mandatory owner stop.
        var owner = new WindowCheckpointRunner(false, false);
        var runner = new DashboardStageRunner(host, owner);
        var failure = new ObservationCheckpointGateException(
            GateResult.Fail(HorizonEvaluation.WindowExhaustedCode, "Synthetic future-window rejection."),
            ObservationStage.PlaceTargetOnSlit);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.OnFaultedAsync(null!, failure, CancellationToken.None));
        Assert.Equal(1, owner.StopCalls);
        Assert.Contains("RUN_MANIFEST_SESSION_MISSING", exception.Message);
        Assert.DoesNotContain(host.Dashboard.Gates.Values, x => x.Code == HorizonEvaluation.WindowExhaustedCode);
    }

    private sealed class WindowCheckpointRunner(bool insideStage, bool failCleanup) : ObservationStageRunnerBase
    {
        public bool ActionAfterCheckpoint { get; private set; }
        public int StopCalls { get; private set; }
        public override async Task<StageResult> ExecuteStageAsync(ObservationStage stage, ObservationContext context, CancellationToken token)
        {
            if (stage == ObservationStage.PlaceTargetOnSlit)
            {
                // Initial plan fits. The real placement route expands its
                // remaining acquisition + science envelope at this same boundary.
                context.RemainingWorstCaseDuration = TimeSpan.FromHours(12);
                if (insideStage)
                {
                    await context.CheckpointAsync(token);
                    ActionAfterCheckpoint = true;
                }
            }
            return new(GateResult.Pass("SIM_PASS", "Synthetic; no equipment."));
        }
        public override Task OnCancelledAsync(ObservationContext context, CancellationToken token)
        {
            StopCalls++;
            return failCleanup ? Task.FromException(new InvalidOperationException("Synthetic stop unconfirmed")) : Task.CompletedTask;
        }
        public override Task OnFaultedAsync(ObservationContext context, Exception cause, CancellationToken token) => OnCancelledAsync(context, token);
    }

    [Fact]
    public void RealCheckpointRejectionPreservesGateAndDoesNotInvokeGenericExceptionRecovery()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "RealObservationStageRunner.cs"));
        var start = source.IndexOf("catch (ObservationCheckpointGateException ex)", StringComparison.Ordinal);
        var body = source[start..source.IndexOf("catch (PhysicalActionGateException ex)", start, StringComparison.Ordinal)];
        Assert.Contains("cancellationToken.ThrowIfCancellationRequested()", body);
        Assert.Contains("new StageResult(ex.Gate)", body);
        Assert.Contains("ex.Gate.Metrics", body);
        Assert.DoesNotContain("CleanupAfterFailureAsync", body);
        Assert.DoesNotContain("PrepareAutomaticStageRecoveryAsync", body);
        Assert.DoesNotContain("STAGE_EXCEPTION", body);
    }

    private sealed class TerminalGateRunner(string? failure, bool failCleanup, bool typedQuality = false,
        bool freshWindow = false, GateResult? producedGate = null,
        ObservationStage failureStage = ObservationStage.AcquireG3SlitField) : ObservationStageRunnerBase
    {
        public List<ObservationStage> Stages { get; } = new();
        public override Task<StageResult> ExecuteStageAsync(ObservationStage stage, ObservationContext context, CancellationToken token)
        {
            Stages.Add(stage);
            if (freshWindow && stage == ObservationStage.PlaceTargetOnSlit)
            {
                var window = new Phd2FreshTargetWindow(3);
                while (!window.Exhausted) window.Observe(measured: false);
                Assert.Equal(3, window.Captures);
                Assert.False(window.Complete);
                return Task.FromResult(new Phd2FreshTargetNotMeasuredException(
                    GateResult.TargetQuality(Phd2FreshTargetWindow.FailureCode,
                        "Formal WCS identity retained, but three fresh frames remain ambiguous; no hardware."),
                    "synthetic-optical-window.json").AfterStop(stopConfirmed: true));
            }
            return Task.FromResult(new StageResult(!freshWindow && stage == failureStage && failure is not null
                ? producedGate ?? (typedQuality ? new TargetQualityEvidenceException(failure, "Synthetic optical failure; no physical hardware.").AfterStop(true).Gate
                    : GateResult.Unknown(failure, "Synthetic quality failure; no physical hardware.")
                  )
                : GateResult.Pass("SIM_STAGE_PASS", "Synthetic stage; no physical hardware.")));
        }
        public override Task OnCancelledAsync(ObservationContext context, CancellationToken token) =>
            failCleanup ? Task.FromException(new InvalidOperationException("Synthetic stop not confirmed")) : Task.CompletedTask;
    }

    private static void InitializeAstrometry()
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

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public void StandaloneAndNativeTargetsKeepIdenticalSciencePolicyWithoutChangingSafety(bool warning, bool weak)
    {
        var settings = Settings();
        settings.AllowSupervisedSlitQualityWarning = warning;
        settings.WeakSupervisionEnabled = weak;
        settings.AllowDegradedSupervisedScience = false;
        settings.RequireSafetyMonitor = true;
        settings.RequireOpenDomeOrRoof = true;
        settings.RequireOpenOpticalCover = true;
        var solver = new PlateSolverRunConfiguration("primary", "blind", "primary-type", "blind-type",
            10, 10, 2, 500, true, 0.1, 5, 3, "", "", "", "", "", "", "", "");
        var standalone = RealRunConfiguration.Capture(settings, solver);
        var native = standalone.WithSequencePlan(new(2, 4, 120, true));
        Assert.Equal(standalone.ScienceQuality, native.ScienceQuality);
        Assert.Equal(standalone.Environment, native.Environment);
        Assert.True(settings.RequireSafetyMonitor && settings.RequireOpenDomeOrRoof && settings.RequireOpenOpticalCover);
        foreach (var configuration in new[] { standalone, native })
        {
            var decision = configuration.ScienceQuality.EvaluateSlitWarning(
                true, true, [8.185094124366973, 3.280662409091443, .8613895484720325], [], 2, 2, 20);
            Assert.Equal(warning, decision.CanProbe);
            Assert.Equal(warning || weak, configuration.ScienceQuality.AllowsMeasuredContinuation);
        }
        settings.AllowSupervisedSlitQualityWarning = !warning;
        Assert.Equal(warning, standalone.ScienceQuality.WarningProbeEnabled);
        Assert.Equal(warning, native.ScienceQuality.WarningProbeEnabled);
        Assert.False(native.MatchesCurrentProfile(settings, solver, out _));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task RecordedWarningDecisionReachesProbeThroughHostAndNativeStages(bool weak)
    {
        InitializeAstrometry();
        var settings = Settings();
        settings.WeakSupervisionEnabled = weak;
        settings.AllowSupervisedSlitQualityWarning = true;
        settings.AllowDegradedSupervisedScience = false;
        settings.ObservationTargetName = "Recorded Capella window";
        settings.ObservationCatalogId = "REPLAY-CAPELLA";
        settings.ObservationNightSetupId = "SIM-NIGHT";
        settings.ObservationRightAscensionDegrees = 0;
        settings.ObservationDeclinationDegrees = 89;
        settings.ObservatoryLatitudeDegrees = 33;
        settings.ObservatoryLongitudeDegreesEast = 120;
        settings.HorizonMinimumDegrees = settings.HorizonStartMarginDegrees = settings.HorizonContinueMarginDegrees = 0;
        settings.ObservationExpectedAtrCameraId = "SIM-ATR";
        settings.ObservationExpectedG3ProfileName = "SIM-G3";
        settings.ObservationExpectedQhyCameraId = "SIM-QHY";
        var solver = new PlateSolverRunConfiguration("primary", "blind", "primary-type", "blind-type",
            10, 10, 2, 500, true, 0.1, 5, 3, "", "", "", "", "", "", "", "");
        var configuration = RealRunConfiguration.Capture(settings, solver);
        using var standalone = new ObservationCoordinatorHost(new SilentNotifier());
        var hostRunner = new RecordedPolicyRunner(configuration);
        await standalone.RunAsync(ObservationPlanFactory.FromSettings(settings, configuration), hostRunner,
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));

        var astrometry = Proxy<IAstrometrySettings>();
        var profile = Proxy<IProfile>((m, _) => m.Name == "get_AstrometrySettings" ? astrometry : Default(m.ReturnType));
        var profiles = Proxy<IProfileService>((m, _) => m.Name == "get_ActiveProfile" ? profile : Default(m.ReturnType));
        using var native = new ObservationCoordinatorHost(new SilentNotifier());
        var nativeRunner = new RecordedPolicyRunner(configuration.WithSequencePlan(new(2, 4, 120, true)));
        var night = new UvexNightSequenceContainer(profiles, native, null!, settings) { StopAtDawn = false };
        night.Add(new UvexTargetObservationContainer(profiles, Proxy<INighttimeCalculator>(), native, null!, settings, () => nativeRunner)
        {
            TargetName = settings.ObservationTargetName, CatalogId = settings.ObservationCatalogId,
            NightSetupId = "SIM-NIGHT", UseRealMode = false, SiteLatitudeDegrees = 33, SiteLongitudeDegreesEast = 120,
            RightAscensionDegrees = 0, DeclinationDegrees = 89, DurationMinutes = 1,
            HorizonMinimumDegrees = 0, HorizonStartMarginDegrees = 0, HorizonContinueMarginDegrees = 0,
            ExpectedAtrCameraId = "SIM-ATR", ExpectedG3ProfileName = "SIM-G3", ExpectedQhyCameraId = "SIM-QHY",
            ScienceFrames = 2, MaximumScienceAttempts = 4,
        });
        await night.Execute(new Progress<ApplicationStatus>(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(ObservationRunCoordinator.Stages, hostRunner.Stages);
        Assert.Equal(hostRunner.Stages, nativeRunner.Stages);
        Assert.Equal(1, hostRunner.ProbeDecisions);
        Assert.Equal(hostRunner.ProbeDecisions, nativeRunner.ProbeDecisions);
        Assert.Equal(ObservationRunState.Completed, standalone.Dashboard.Run.State);
        Assert.Equal(standalone.Dashboard.Run.State, native.Dashboard.Run.State);
        Assert.Equal(GateSeverity.Warning, native.Dashboard.Gates[ObservationStage.PlaceTargetOnSlit].Severity);
    }

    // Replays policy/route only. Environment and hardware responses are test
    // doubles, and a probe decision is not an exposure or a science acceptance.
    private sealed class RecordedPolicyRunner(RealRunConfiguration configuration) : ObservationStageRunnerBase
    {
        public List<ObservationStage> Stages { get; } = [];
        public int ProbeDecisions;
        public override Task<StageResult> ExecuteStageAsync(ObservationStage stage, ObservationContext context, CancellationToken token)
        {
            Stages.Add(stage);
            if (stage == ObservationStage.PlaceTargetOnSlit)
            {
                var decision = configuration.ScienceQuality.EvaluateSlitWarning(true, true,
                    [8.185094124366973, 3.280662409091443, .8613895484720325], [], 2, 2, 20);
                return Task.FromResult(new StageResult(decision.CanProbe
                    ? GateResult.Warn("REPLAY_MEASURED_WARNING_PROBE", "Recorded optical decision; no hardware.")
                    : GateResult.TargetQuality("REPLAY_STRICT_REJECTED", "Strict preference not met.")));
            }
            if (stage == ObservationStage.SelectAtrExposure) ProbeDecisions++;
            return Task.FromResult(new StageResult(GateResult.Pass("REPLAY_STAGE", "Synthetic gates; no hardware.")));
        }
    }

    [Theory]
    [InlineData("success")]
    [InlineData("optical-fail")]
    [InlineData("budget-empty")]
    [InlineData("repair-failed")]
    [InlineData("stop-unconfirmed")]
    [InlineData("unsafe")]
    [InlineData("cancel")]
    public async Task NativeClockRepairReacquiresSameCardBeforeNextTargetWithoutDoubleCounting(string mode)
    {
        InitializeAstrometry();
        var settings = Settings();
        var profile = Proxy<IProfile>((m, _) => m.Name == "get_AstrometrySettings" ? Proxy<IAstrometrySettings>() : Default(m.ReturnType));
        var profiles = Proxy<IProfileService>((m, _) => m.Name == "get_ActiveProfile" ? profile : Default(m.ReturnType));
        using var host = new ObservationCoordinatorHost(new SilentNotifier());
        var night = new UvexNightSequenceContainer(profiles, host, null!, settings) { StopAtDawn = false };
        var calls = new List<string>();
        var runners = new List<ClockBoundaryRunner>();
        var next = new TerminalGateRunner(null, false);
        for (var i = 0; i < 2; i++)
        {
            var isFirst = i == 0;
            night.Add(new UvexTargetObservationContainer(profiles, Proxy<INighttimeCalculator>(), host, null!, settings, () =>
            {
                if (!isFirst) { calls.Add("next-target"); return next; }
                var runner = new ClockBoundaryRunner(mode, runners.Count, calls, night);
                runners.Add(runner); return runner;
            })
            {
                TargetName = isFirst ? "Clock target" : "Next target", CatalogId = $"SIM-{i}", NightSetupId = "SIM-NIGHT",
                UseRealMode = false, SiteLatitudeDegrees = 33, SiteLongitudeDegreesEast = 120,
                RightAscensionDegrees = 0, DeclinationDegrees = 89, DurationMinutes = 5,
                HorizonMinimumDegrees = 0, HorizonStartMarginDegrees = 0, HorizonContinueMarginDegrees = 0,
                ExpectedAtrCameraId = "SIM-ATR", ExpectedG3ProfileName = "SIM-G3", ExpectedQhyCameraId = "SIM-QHY",
                ScienceFrames = 2, MaximumScienceAttempts = 4, ForceGuideCalibration = true,
                FailurePolicy = SpectroscopyTargetFailurePolicy.SkipQualityFailure,
            });
        }
        var error = await Record.ExceptionAsync(() => night.Execute(new Progress<ApplicationStatus>(), default).WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.IsNotType<TimeoutException>(error);
        var continued = mode is "success" or "optical-fail";
        Assert.Equal(continued ? 2 : 1, runners.Count);
        Assert.Equal(mode == "stop-unconfirmed" ? 0 : 1, calls.Count(x => x == "repair"));
        Assert.Equal(mode is "success" or "optical-fail" or "budget-empty", next.Stages.Count > 0);
        if (continued)
        {
            Assert.Equal(1, runners[1].Remaining!.ScienceFrames);
            Assert.Equal(2, runners[1].Remaining!.MaximumAttempts);
            Assert.False(runners[1].Remaining!.ForceGuideCalibration);
            Assert.Equal(runners[0].Session!.TargetPlan!.PlannedStartUtc + TimeSpan.FromMinutes(5), runners[1].Remaining!.TargetDeadlineUtc);
            Assert.Equal(2, runners[1].Session!.ClockBudget!.Phd2!.Attempts);
            Assert.Equal(30, runners[1].Session!.ClockBudget!.Phd2!.Distance);
            Assert.Equal(runners[0].Started, runners[1].Session!.ClockBudget!.Phd2!.StartedUtc);
            Assert.True(calls.IndexOf("repair") < calls.IndexOf("segment-1-ValidateNightSetup"));
            Assert.True(calls.IndexOf("segment-1-AcquireG3SlitField") < calls.IndexOf("next-target"));
            if (mode == "success")
            {
                Assert.Contains("segment-1-PlaceTargetOnSlit", calls);
                Assert.Contains("segment-1-RunScienceBlock", calls);
                Assert.Equal(2, runners[1].Session!.Accepted);
                Assert.Equal(3, runners[1].Session!.Attempts);
                Assert.Equal(240, runners[1].Session!.AcceptedSeconds);
            }
            else Assert.Equal(1, runners[1].Session!.Accepted);
        }
        using var journal = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(night.JournalPath));
        Assert.Equal(continued ? 1 : 0, journal.RootElement.GetProperty("clockContinuations").GetArrayLength());
        var targetOutcomes = journal.RootElement.GetProperty("outcomes").EnumerateArray().Where(x => x.TryGetProperty("target", out _)).ToArray();
        Assert.Equal(next.Stages.Count > 0 ? 2 : 1, targetOutcomes.Length);
        Assert.Equal("Clock target", targetOutcomes[0].GetProperty("target").GetString());
        Assert.Equal(mode == "success" ? "Completed" : mode == "optical-fail" ? "SkippedQualityFailure" :
            mode == "budget-empty" ? "SkippedClockContinuationBudgetExhausted" : "Stopped", targetOutcomes[0].GetProperty("outcome").GetString());
        if (host.SafetyStandby is { } guard) { night.EndNightCommand.Execute(null); await guard.Completion.WaitAsync(TimeSpan.FromSeconds(3)); }
    }

    private sealed class ClockBoundaryRunner(string mode, int segment, List<string> calls, UvexNightSequenceContainer night)
        : ObservationStageRunnerBase, INativeTargetBoundary
    {
        public NativeTargetSession? Session;
        public NativeSequencePlan? Remaining;
        public DateTimeOffset Started = DateTimeOffset.UtcNow.AddSeconds(-10);
        public void AttachNativeTarget(NativeTargetSession? session) { Session = session; Remaining = session!.RemainingPlan(); }
        public void RecordMeridianSegment(NativeTargetSession session, string runId) =>
            session.RecordSegment(runId, segment == 0 || mode == "success" ? 1 : 0,
                segment == 0 ? 2 : mode == "success" ? 1 : 0, 0, segment == 0 || mode == "success" ? 120 : 0);
        public ClockContinuationBudget CaptureClockContinuationBudget() => new(null,
            new(2, 30, Started, 8, 100, mode == "budget-empty" ? 1 : 600), 0, 0, null, 1200, 0, 0, 1, 8, 0, 0);
        public Task<IReadOnlyList<string>> ConfirmSequenceIdleAsync(CancellationToken token, bool requireSettledMotion = true)
        { calls.Add("stopped"); return Task.FromResult<IReadOnlyList<string>>(mode == "stop-unconfirmed" ? new[] { "Synthetic unknown stop" } : Array.Empty<string>()); }
        public async Task<GateResult> RecoverMountClockAtIdleBoundaryAsync(ObservationPlan failedPlan, CancellationToken token)
        {
            Assert.Contains("stopped", calls);
            Assert.Contains("ClockRecoveryIntent", await File.ReadAllTextAsync(night.JournalPath, token));
            calls.Add("repair");
            if (mode == "unsafe") night.OnNightUnsafe();
            if (mode == "cancel") await night.Interrupt();
            return mode == "repair-failed" ? GateResult.Fail("MOUNT_CLOCK_RECOVERY_POSTCHECK_FAILED", "synthetic") : GateResult.Pass("MOUNT_CLOCK_VALID", "synthetic");
        }
        public override Task<StageResult> ExecuteStageAsync(ObservationStage stage, ObservationContext context, CancellationToken token)
        {
            calls.Add($"segment-{segment}-{stage}");
            return Task.FromResult(new StageResult(segment == 0 && stage == ObservationStage.RunScienceBlock
                ? GateResult.Unknown("MOUNT_CLOCK_RECHECK_REQUIRED", "Recorded two-day offset; no hardware")
                : segment == 1 && mode == "optical-fail" && stage == ObservationStage.AcquireG3SlitField
                    ? GateResult.TargetQuality("G3_POST_WCS_SHORT_UNCONFIRMED", "Synthetic reacquisition failure")
                    : GateResult.Pass("SIM_STAGE", "No physical acquisition")));
        }
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
