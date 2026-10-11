using System.Reflection;
using NINA.Astrometry.Interfaces;
using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using NINA.Sequencer;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.SequenceItem.Utility;
using NINA.Sequencer.Serialization;
using Newtonsoft.Json.Linq;
using UvexAdv.Nina.Plugin.SequenceItems;
using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

/// <summary>Exercise the distributed files, not an independently rebuilt example.</summary>
public sealed class DistributedSequenceTemplateTests
{
    private const string Full = "OpenAstroSpec-Spectroscopy-MultiTarget.json";
    private const string Block = "OpenAstroSpec-Spectroscopy-TargetBlock.template.json";

    [Fact]
    public void NativePopulateDoesNotMutateSharedPrototypeCatalogueOrLeakItsCoordinateProvenance()
    {
        NativeAstrometry();
        using var fixture = new Fixture();
        var original = new TargetCatalogMetadata("old profile target", "old catalog", 17, 32,
            "old source", "star", 4, DateTimeOffset.UtcNow, CoordinateProvenance:
            new("old convention", "old source", 17, 32, 2461322, true, 20, "old epoch"));
        var prototype = Night(fixture.Load(Block)).Items.OfType<UvexTargetObservationContainer>().First();
        prototype.CatalogMetadata = original;
        var targets = new List<UvexTargetObservationContainer>();
        for (var index = 0; index < 10; index++)
        {
            var target = (UvexTargetObservationContainer)prototype.Clone();
            // SequenceJsonConverter populates a clone of an already populated MEF prototype.
            // A missing optional provenance must not survive from that prototype.
            Newtonsoft.Json.JsonConvert.PopulateObject(new JObject
            {
                ["TargetName"] = $"target {index}", ["CatalogId"] = $"HD {index}",
                ["RightAscensionDegrees"] = 45 + index, ["DeclinationDegrees"] = 38,
                ["CatalogMetadata"] = new JObject
                {
                    ["TargetName"] = $"target {index}", ["CatalogId"] = $"HD {index}",
                    ["RightAscensionDegrees"] = 45 + index, ["DeclinationDegrees"] = 38,
                    ["Source"] = "SIMBAD", ["ObjectType"] = "star", ["VisualMagnitude"] = 6.1,
                    ["CapturedUtc"] = "2026-10-09T16:00:00Z",
                },
            }.ToString(), target);
            targets.Add(target);
        }
        Assert.Equal("old profile target", prototype.CatalogMetadata!.TargetName);
        for (var index = 0; index < targets.Count; index++)
        {
            Assert.Equal($"target {index}", targets[index].CatalogMetadata!.TargetName);
            Assert.Equal(45 + index, targets[index].CatalogMetadata!.RightAscensionDegrees);
            Assert.Null(targets[index].CatalogMetadata!.CoordinateProvenance);
            Assert.NotSame(original, targets[index].CatalogMetadata);
        }
        Newtonsoft.Json.JsonConvert.PopulateObject("{\"TargetName\":\"no catalogue\"}", prototype);
        Assert.Null(prototype.CatalogMetadata);
    }

    [Theory]
    [InlineData("2026-10-10T01:00:00+08:00")]
    [InlineData("2026-10-09T17:00:00Z")]
    [InlineData("2026-10-09T12:00:00-05:00")]
    [InlineData("2026-10-09T22:30:00.1234567+05:30")]
    public void NativeDeadlineImportAndRepeatedSaveLoadPreserveTheAbsoluteInstant(string deadline)
    {
        using var fixture = new Fixture();
        var loaded = fixture.Load(Full);
        Night(loaded).EndAtIso8601 = deadline;
        var expected = DateTimeOffset.Parse(deadline, System.Globalization.CultureInfo.InvariantCulture);
        for (var pass = 0; pass < 3; pass++)
        {
            loaded = fixture.Converter.Deserialize(fixture.Converter.Serialize(loaded));
            var night = Night(loaded);
            Assert.True(night.Validate(), string.Join("; ", night.Issues));
            Assert.Equal(expected, DateTimeOffset.Parse(night.EndAtIso8601,
                System.Globalization.CultureInfo.InvariantCulture));
            Assert.Matches(@"(Z|[+-]\d{2}:\d{2})$", night.EndAtIso8601);
            Assert.Equal(4, night.Items.Count);
            Assert.Single(night.Triggers);
        }
        Assert.Equal(0, fixture.TelescopeCalls);
        Assert.Equal(0, fixture.ProfileWritesAfterSetup);
    }

    [Theory]
    [InlineData("2026-10-10T01:00:00")]
    [InlineData("10/10/2026 01:00:00")]
    [InlineData("not a deadline")]
    public void NativeDeadlineImportDoesNotInventAnOffsetForAmbiguousOrInvalidText(string deadline)
    {
        using var fixture = new Fixture();
        var loaded = fixture.Load(Full);
        Night(loaded).EndAtIso8601 = deadline;
        var restored = Night(fixture.Converter.Deserialize(fixture.Converter.Serialize(loaded)));
        Assert.False(restored.Validate());
        Assert.Contains(restored.Issues, issue => issue.Contains("截止时间必须包含时区"));
        Assert.Equal(0, fixture.TelescopeCalls);
    }

    [Theory]
    [InlineData(Full)]
    [InlineData(Block)]
    public void NativeImportAndRepeatedSaveLoadPreserveAllStagesAndOneScopedFlip(string file)
    {
        using var fixture = new Fixture();
        var loaded = fixture.Load(file);
        for (var pass = 0; pass < 3; pass++)
        {
            AssertTemplate(loaded);
            loaded = fixture.Converter.Deserialize(fixture.Converter.Serialize(loaded));
        }
        AssertTemplate(loaded);
        Assert.Equal(0, fixture.ProfileWritesAfterSetup);
        Assert.Equal(0, fixture.TelescopeCalls);
    }

    [Theory]
    [InlineData("00:30")]
    [InlineData("05:00")]
    [InlineData("23:45:30")]
    public void ClockOnlyDeadlineRoundTripsClonesAndLocksWithoutDateOrHardware(string clock)
    {
        using var fixture = new Fixture();
        var loaded = fixture.Load(Full);
        Night(loaded).EndAtIso8601 = clock;
        Night(loaded).HoldOpenAfterTargets = true;
        for (var i = 0; i < 3; i++)
        {
            loaded = fixture.Converter.Deserialize(fixture.Converter.Serialize(loaded));
            Assert.Equal(clock, Night(loaded).EndAtIso8601);
            Assert.True(Night(loaded).Validate(), string.Join("; ", Night(loaded).Issues));
        }
        var night = Night(loaded);
        var clone = Assert.IsType<UvexNightSequenceContainer>(night.Clone());
        Assert.Equal(clock, clone.EndAtIso8601);
        Assert.Null(clone.ApplyNight(new(1, 1, 0)).DeadlineUtc);
        night.LockDeadline(DateTimeOffset.Parse("2026-10-10T19:00:00Z"), TimeZoneInfo.Utc);
        var locked = night.ApplyNight(new(1, 1, 0)).DeadlineUtc;
        Assert.NotNull(locked);
        night.EndAtIso8601 = "";
        Assert.Equal(locked, night.ApplyNight(new(1, 1, 0)).DeadlineUtc);
        Assert.Equal(0, fixture.ProfileWritesAfterSetup);
        Assert.Equal(0, fixture.TelescopeCalls);
    }

    [Fact]
    public void FullSequenceHasNativeAreasAndExplicitlyDisabledStartupCards()
    {
        using var fixture = new Fixture();
        var root = Assert.IsType<SequenceRootContainer>(fixture.Load(Full));
        Assert.Equal(3, root.Items.Count);
        var start = Assert.IsType<StartAreaContainer>(root.Items[0]);
        var middle = Assert.IsType<TargetAreaContainer>(root.Items[1]);
        var end = Assert.IsType<EndAreaContainer>(root.Items[2]);
        Assert.Empty(root.Triggers);
        Assert.False(Assert.IsType<UvexStartSafetyMonitorItem>(start.Items[0]).StartMonitoring);
        Assert.False(Assert.IsType<UvexSelectRealModeItem>(start.Items[1]).EnableRealMode);
        Assert.All(start.Items.Skip(2).Concat(end.Items), item => Assert.IsType<Annotation>(item));
        Assert.IsType<UvexNightSequenceContainer>(Assert.Single(middle.Items));
        Assert.All(root.Items, item => Assert.Same(root, item.Parent));
        Assert.All(start.Items, item => Assert.Same(start, item.Parent));
        Assert.All(end.Items, item => Assert.Same(end, item.Parent));
    }

    [Fact]
    public void CompleteSequenceAndReusableBlockHaveTheSameAcquisitionChoices()
    {
        using var fixture = new Fixture();
        var full = Night(fixture.Load(Full));
        var block = Night(fixture.Load(Block));
        Assert.Equal(full.StopAtDawn, block.StopAtDawn);
        Assert.Equal(full.DawnSunAltitudeDegrees, block.DawnSunAltitudeDegrees);
        Assert.Equal(full.EndAtIso8601, block.EndAtIso8601);
        Assert.Equal(full.Items.OfType<UvexTargetObservationContainer>().Select(Choices),
            block.Items.OfType<UvexTargetObservationContainer>().Select(Choices));
        var clone = Assert.IsType<UvexNightSequenceContainer>(block.Clone());
        AssertTemplate(clone);
        ((UvexTargetObservationContainer)clone.Items[0]).ScienceFrames = 5;
        Assert.Equal(3, ((UvexTargetObservationContainer)block.Items[0]).ScienceFrames);
    }

    [Theory]
    [InlineData(Full)]
    [InlineData(Block)]
    public void TemplatesInheritLocalIdentityButOverrideRealModeAndClearOldTargetMetadata(string file)
    {
        using var fixture = new Fixture();
        Assert.True(fixture.Settings.ObservationUseRealMode);
        var night = Night(fixture.Load(file));
        foreach (var target in night.Items.OfType<UvexTargetObservationContainer>())
        {
            Assert.False(target.UseRealMode);
            Assert.Equal("OFFLINE-LOCAL-NIGHT", target.NightSetupId);
            Assert.Equal("OFFLINE-LOCAL-ATR", target.ExpectedAtrCameraId);
            Assert.Equal("OFFLINE-LOCAL-PHD2", target.ExpectedG3ProfileName);
            Assert.Equal("OFFLINE-LOCAL-QHY", target.ExpectedQhyCameraId);
            Assert.Equal(33, target.SiteLatitudeDegrees);
            Assert.Equal(120, target.SiteLongitudeDegreesEast);
            Assert.Equal(19, target.SiteElevationMeters);
            Assert.Equal(30, target.HorizonMinimumDegrees);
            Assert.Null(target.CatalogMetadata);
            Assert.Equal(string.Empty, target.CatalogId);
            Assert.Equal(TargetObservabilityClass.DirectStellar, target.TargetObservability);
        }
        Assert.True(night.Validate(), string.Join("; ", night.Issues));
        Assert.Equal(0, fixture.ProfileWritesAfterSetup);
    }

    [Theory]
    [InlineData(Full)]
    [InlineData(Block)]
    public void DistributedFilesHaveNoMachineBindingsRuntimeStateOrExtraPluginDependencies(string file)
    {
        var json = JObject.Parse(File.ReadAllText(TemplatePath(file)));
        var properties = json.Descendants().OfType<JProperty>().ToArray();
        var forbidden = new[] { "Target", "NightSetupId", "ExpectedAtrCameraId", "ExpectedG3ProfileName",
            "ExpectedQhyCameraId", "SiteLatitudeDegrees", "SiteLongitudeDegreesEast", "SiteElevationMeters",
            "HorizonMinimumDegrees", "HorizonStartMarginDegrees", "HorizonContinueMarginDegrees",
            "RealModeCommissioned", "ObservationUseRealMode", "WeakSupervisionEnabled", "Status",
            "JournalPath", "ManifestPath", "RequestedSide", "MinutesAfterMeridian", "UseSideOfPier" };
        Assert.DoesNotContain(properties, p => forbidden.Contains(p.Name));
        Assert.All(properties.Where(p => p.Name == "UseRealMode"), p => Assert.False(p.Value.Value<bool>()));
        Assert.All(properties.Where(p => p.Name == "Attempts"), p => Assert.Equal(1, p.Value.Value<int>()));
        var allowedTypes = new[]
        {
            "NINA.Sequencer.Container.SequenceRootContainer, NINA.Sequencer",
            "NINA.Sequencer.Container.StartAreaContainer, NINA.Sequencer",
            "NINA.Sequencer.Container.TargetAreaContainer, NINA.Sequencer",
            "NINA.Sequencer.Container.EndAreaContainer, NINA.Sequencer",
            "NINA.Sequencer.Container.ExecutionStrategy.SequentialStrategy, NINA.Sequencer",
            "NINA.Sequencer.SequenceItem.Utility.Annotation, NINA.Sequencer",
            "UvexAdv.Nina.Plugin.SequenceItems.UvexNightSequenceContainer, UvexAdv.Nina.Plugin",
            "UvexAdv.Nina.Plugin.SequenceItems.UvexTargetObservationContainer, UvexAdv.Nina.Plugin",
            "UvexAdv.Nina.Plugin.SequenceItems.ObservationStageMarkerItem, UvexAdv.Nina.Plugin",
            "UvexAdv.Nina.Plugin.SequenceItems.SpectroscopyMeridianFlipTrigger, UvexAdv.Nina.Plugin",
            "UvexAdv.Nina.Plugin.SequenceItems.UvexObservatoryCloseoutItem, UvexAdv.Nina.Plugin",
            "UvexAdv.Nina.Plugin.SequenceItems.UvexStartSafetyMonitorItem, UvexAdv.Nina.Plugin",
            "UvexAdv.Nina.Plugin.SequenceItems.UvexSelectRealModeItem, UvexAdv.Nina.Plugin",
        };
        Assert.All(properties.Where(p => p.Name == "$type"), p => Assert.Contains(p.Value.Value<string>(), allowedTypes));
    }

    [Theory]
    [InlineData(Full)]
    [InlineData(Block)]
    public async Task DistributedTemplateRunsThreeTargetsInOrderThroughNativeEngineWithoutHardware(string file)
    {
        NativeAstrometry();
        using var fixture = new Fixture();
        var loaded = fixture.Load(file);
        var night = Night(loaded);
        // Only the in-memory test copy gets a zenith target and daytime rehearsal override.
        // The shipped file keeps dawn protection and explicitly labelled placeholders.
        night.StopAtDawn = false;
        night.EndAtIso8601 = ""; // Daytime execution rehearsal only, not the shipped deadline.
        var jd = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 86400000d + 2440587.5;
        var zenithRa = ((280.46061837 + 360.98564736629 * (jd - 2451545) + 120) % 360 + 360) % 360;
        var targets = night.Items.OfType<UvexTargetObservationContainer>().ToArray();
        foreach (var target in targets)
        {
            target.RightAscensionDegrees = zenithRa;
            target.DeclinationDegrees = 33;
        }
        Assert.True(loaded.Validate(), string.Join("; ", loaded.Issues));
        await Assert.IsAssignableFrom<SequenceContainer>(loaded)
            .Execute(new Progress<ApplicationStatus>(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.All(targets, target =>
        {
            Assert.Equal("完成", target.LastOutcome);
            Assert.All(target.Items, item => Assert.Equal(SequenceEntityStatus.FINISHED, item.Status));
        });
        Assert.Equal(ObservationRunState.Completed, fixture.Host.Dashboard.Run.State);
        Assert.Contains("模拟结束", night.NightState);
        var journal = JObject.Parse(await File.ReadAllTextAsync(night.JournalPath));
        var completed = journal["outcomes"]!.Children().Where(x => (string?)x["outcome"] == "Completed").ToArray();
        Assert.Equal(targets.Select(x => x.TargetName), completed.Select(x => (string)x["target"]!));
        Assert.Equal(3, completed.Select(x => (string)x["runId"]!).Distinct().Count());
        Assert.False(journal["outcomes"]!.Last!["physicalHardware"]!.Value<bool>());
        Assert.Equal(0, fixture.TelescopeCalls);
        Assert.Equal(0, fixture.ProfileWritesAfterSetup);
    }

    private static void AssertTemplate(ISequenceContainer loaded)
    {
        var night = Night(loaded);
        Assert.Equal(4, night.Items.Count);
        Assert.True(Assert.IsType<UvexObservatoryCloseoutItem>(night.Items.Last()).CloseAfterTargets);
        Assert.True(night.StopAtDawn);
        Assert.Equal(-12, night.DawnSunAltitudeDegrees);
        Assert.Equal("05:00", night.EndAtIso8601);
        var flip = Assert.IsType<SpectroscopyMeridianFlipTrigger>(Assert.Single(night.Triggers));
        Assert.Same(night, flip.Parent);
        Assert.Equal(120, flip.BoundaryReserveSeconds);
        Assert.Null(flip.RequestedSide);
        Assert.IsType<SequentialContainer>(flip.TriggerRunner);
        Assert.Empty(flip.TriggerRunner.Items);
        Assert.False(flip.ShouldTrigger(null!, null!));
        Assert.Empty(night.Conditions);
        foreach (var target in night.Items.OfType<UvexTargetObservationContainer>())
        {
            Assert.Same(night, target.Parent);
            Assert.Equal(target.TargetName, target.Target.TargetName);
            Assert.Equal(target.RightAscensionDegrees, target.Target.InputCoordinates.Coordinates.RADegrees);
            Assert.Equal(target.DeclinationDegrees, target.Target.InputCoordinates.Coordinates.Dec);
            Assert.False(target.UseRealMode);
            Assert.Equal(3, target.ScienceFrames);
            Assert.Equal(6, target.MaximumScienceAttempts);
            Assert.Equal(300, target.FixedExposureSeconds);
            Assert.Equal(SpectroscopyTargetFailurePolicy.EndNight, target.FailurePolicy);
            Assert.Empty(target.Conditions);
            Assert.Empty(target.Triggers);
            Assert.Empty(SpectroscopyMeridianFlipTrigger.ScopeIssues(target));
            Assert.Same(flip, Assert.Single(SpectroscopyMeridianFlipTrigger.InScope(target)));
            Assert.Equal(ObservationRunCoordinator.Stages, target.Items.Cast<ObservationStageMarkerItem>().Select(x => x.Stage));
            Assert.All(target.Items, item => Assert.Same(target, item.Parent));
        }
    }

    private static object Choices(UvexTargetObservationContainer t) => new
    { t.TargetName, t.CatalogId, t.RightAscensionDegrees, t.DeclinationDegrees, t.DurationMinutes,
        t.TargetObservability, t.ScienceFrames, t.MaximumScienceAttempts, t.FixedExposureSeconds, t.FailurePolicy, t.UseRealMode };
    private static string TemplatePath(string name) => Path.Combine(AppContext.BaseDirectory, "SequenceTemplates", name);
    private static UvexNightSequenceContainer Night(ISequenceContainer container) =>
        container is UvexNightSequenceContainer night ? night :
        Assert.IsType<UvexNightSequenceContainer>(Assert.Single(Assert.IsType<TargetAreaContainer>(container.Items[1]).Items));

    private static void NativeAstrometry()
    {
        const string nina = @"C:\Program Files\N.I.N.A. - Nighttime Imaging 'N' Astronomy";
        try { System.Runtime.InteropServices.NativeLibrary.SetDllImportResolver(typeof(NINA.Astrometry.NOVAS).Assembly,
            (name, _, _) => name switch
            {
                "NOVAS31lib.dll" => System.Runtime.InteropServices.NativeLibrary.Load(Path.Combine(nina, "External", "x64", "NOVAS", name)),
                "SOFAlib.dll" => System.Runtime.InteropServices.NativeLibrary.Load(Path.Combine(nina, "External", "x64", "SOFA", name)),
                _ => IntPtr.Zero,
            }); }
        catch (InvalidOperationException) { /* Another astrometry-only fixture owns this resolver. */ }
    }

    private sealed class Fixture : IDisposable
    {
        public UvexPluginSettings Settings { get; }
        public ObservationCoordinatorHost Host { get; } = new(new SilentNotifier());
        public SequenceJsonConverter Converter { get; }
        public int ProfileWritesAfterSetup { get; private set; }
        public int TelescopeCalls { get; private set; }

        public Fixture()
        {
            var values = new Dictionary<string, object?>();
            var accessor = Proxy<IPluginOptionsAccessor>((m, args) =>
            {
                if (m.Name.StartsWith("GetValue")) return values.TryGetValue((string)args[0]!, out var value) ? value : args[1];
                if (m.Name.StartsWith("SetValue")) { ProfileWritesAfterSetup++; values[(string)args[0]!] = args[1]; return null; }
                return Default(m.ReturnType);
            });
            var astrometry = Proxy<IAstrometrySettings>();
            var flip = Proxy<IMeridianFlipSettings>((m, _) => m.Name switch
            {
                "get_UseSideOfPier" => true, "get_MinutesAfterMeridian" => 5d,
                "get_MaxMinutesAfterMeridian" => 10d, "get_PauseTimeBeforeMeridian" => 0d,
                _ => Default(m.ReturnType),
            });
            var profile = Proxy<IProfile>((m, _) => m.Name switch
            {
                "get_AstrometrySettings" => astrometry, "get_MeridianFlipSettings" => flip,
                _ => Default(m.ReturnType),
            });
            var profiles = Proxy<IProfileService>((m, _) => m.Name == "get_ActiveProfile" ? profile : Default(m.ReturnType));
            Settings = new(profiles, accessor)
            {
                ObservationUseRealMode = true, ObservationTargetName = "old profile target",
                ObservationCatalogId = "old catalog", ObservationTargetObservability = TargetObservabilityClass.ExtendedNebula,
                ObservationNightSetupId = "OFFLINE-LOCAL-NIGHT", ObservationExpectedAtrCameraId = "OFFLINE-LOCAL-ATR",
                ObservationExpectedG3ProfileName = "OFFLINE-LOCAL-PHD2", ObservationExpectedQhyCameraId = "OFFLINE-LOCAL-QHY",
                ObservatoryLatitudeDegrees = 33, ObservatoryLongitudeDegreesEast = 120, ObservatoryElevationMeters = 19,
            };
            ProfileWritesAfterSetup = 0;
            var telescope = Proxy<ITelescopeMediator>((_, _) =>
            {
                TelescopeCalls++;
                throw new InvalidOperationException("An offline template must not call a hardware mediator.");
            });
            object Container(Type type) => type == typeof(UvexNightSequenceContainer)
                ? new UvexNightSequenceContainer(profiles, Host, null!, Settings)
                : type == typeof(UvexTargetObservationContainer)
                    ? new UvexTargetObservationContainer(profiles, Proxy<INighttimeCalculator>(), Host, null!, Settings)
                    : type == typeof(SequenceRootContainer) ? new SequenceRootContainer()
                    : type == typeof(StartAreaContainer) ? new StartAreaContainer()
                    : type == typeof(TargetAreaContainer) ? new TargetAreaContainer()
                    : type == typeof(EndAreaContainer) ? new EndAreaContainer()
                    : type == typeof(SequentialContainer) ? new SequentialContainer()
                    : throw new InvalidOperationException($"Unexpected container {type}");
            var factory = Proxy<ISequencerFactory>((m, _) => m.Name switch
            {
                "GetContainer" => Container(m.GetGenericArguments()[0]),
                "GetItem" when m.GetGenericArguments()[0] == typeof(ObservationStageMarkerItem) => new ObservationStageMarkerItem(),
                "GetItem" when m.GetGenericArguments()[0] == typeof(Annotation) => new Annotation(),
                "GetItem" when m.GetGenericArguments()[0] == typeof(UvexObservatoryCloseoutItem) => new UvexObservatoryCloseoutItem(),
                "GetItem" when m.GetGenericArguments()[0] == typeof(UvexStartSafetyMonitorItem) => new UvexStartSafetyMonitorItem(profiles, Host, null!, Settings),
                "GetItem" when m.GetGenericArguments()[0] == typeof(UvexSelectRealModeItem) => new UvexSelectRealModeItem(profiles, Host, Settings),
                "GetTrigger" when m.GetGenericArguments()[0] == typeof(SpectroscopyMeridianFlipTrigger) =>
                    new SpectroscopyMeridianFlipTrigger(profiles, null!, telescope, null!, null!, null!),
                _ => Default(m.ReturnType),
            });
            Converter = new(factory);
        }

        public ISequenceContainer Load(string file) => Converter.Deserialize(File.ReadAllText(TemplatePath(file)));
        public void Dispose() => Host.Dispose();
    }

    private sealed class SilentNotifier : IObservationAttentionNotifier
    {
        public void Notify(ObservationAttentionNotification notification) { }
        public void ClearActiveIndicator() { }
        public void Dispose() { }
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
        public Func<MethodInfo, object?[], object?> Handler { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args ?? []);
    }
}
