using System.Reflection;
using NINA.Astrometry.Interfaces;
using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using NINA.Sequencer;
using NINA.Sequencer.Container;
using NINA.Sequencer.Conditions;
using NINA.Sequencer.SequenceItem.Connect;
using NINA.Sequencer.SequenceItem.SafetyMonitor;
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

    // Optional maintenance inputs stay outside Git. They are parsed with native
    // types and fake mediators, never loaded into a running N.I.N.A. frontend.
    public static IEnumerable<object[]> AuditFiles()
    {
        yield return [TemplatePath(Full)];
        yield return [TemplatePath(Block)];
        if (Environment.GetEnvironmentVariable("UVEX_SEQUENCE_AUDIT_DIRECTORY") is { Length: > 0 } directory)
        {
            var files = Directory.GetFiles(directory, "*.json");
            Assert.Equal(5, files.Length); // Three personal nights and both saved templates.
            foreach (var file in files.Order()) yield return [file];
        }
    }

    [Theory]
    [MemberData(nameof(AuditFiles))]
    public void SavedSequenceOfflineAuditPreservesTargetsClocksAndCloseout(string path)
    {
        using var fixture = new Fixture();
        var json = JObject.Parse(File.ReadAllText(path));
        var sourceTargets = json.DescendantsAndSelf().OfType<JObject>()
            .Where(x => x["$type"]?.Value<string>()?.StartsWith(typeof(UvexTargetObservationContainer).FullName!) == true).ToArray();
        var loaded = fixture.Converter.Deserialize(json.ToString());
        var night = Night(loaded);
        // Native HMS/DMS round-tripping introduces sub-femtodegree floating
        // noise at 0/0 placeholders. Compare coordinates to 1e-8 degrees, while
        // keeping all non-coordinate choices exact.
        static string AuditChoices(UvexTargetObservationContainer target)
        {
            static double Coordinate(double number) => Math.Abs(number) < 0.000000005 ? 0d : Math.Round(number, 8);
            var value = JObject.FromObject(Choices(target));
            // JObject treats very close doubles as equal and can ignore a
            // replacement; remove first to actually normalize the test value.
            value.Remove(nameof(target.RightAscensionDegrees));
            value.Remove(nameof(target.DeclinationDegrees));
            value[nameof(target.RightAscensionDegrees)] = Coordinate(target.RightAscensionDegrees);
            value[nameof(target.DeclinationDegrees)] = Coordinate(target.DeclinationDegrees);
            return value.ToString();
        }
        var choices = night.Items.OfType<UvexTargetObservationContainer>().Select(AuditChoices).ToArray();
        var targetSettings = night.Items.OfType<UvexTargetObservationContainer>()
            .Select(x => (x.ForceGuideCalibration, x.AutomaticGuideRecovery, x.GuideStarWaitSeconds)).ToArray();
        Assert.Equal(sourceTargets.Length, choices.Length);
        foreach (var (target, source) in night.Items.OfType<UvexTargetObservationContainer>().Zip(sourceTargets))
        {
            Assert.Equal(source["TargetName"]!.Value<string>(), target.TargetName);
            Assert.Equal(source["FixedExposureSeconds"]!.Value<double>(), target.FixedExposureSeconds);
            Assert.Equal(source["ScienceFrames"]!.Value<int>(), target.ScienceFrames);
            Assert.Equal(source["RightAscensionDegrees"]!.Value<double>(), target.RightAscensionDegrees, 8);
            Assert.Equal(source["DeclinationDegrees"]!.Value<double>(), target.DeclinationDegrees, 8);
        }
        var policy = (night.EndAtIso8601, night.StopAtDawn, night.DawnSunAltitudeDegrees,
            night.EffectiveHoldOpenAfterTargets, night.HomeBeforeParkOnFinalize, night.MountHomeTimeoutSeconds);
        var start = (loaded as SequenceRootContainer)?.Items.OfType<StartAreaContainer>().Single();
        var wait = start?.Items.OfType<WaitForTime>().SingleOrDefault();
        var clock = wait is null ? null : (wait.Hours, wait.Minutes, wait.Seconds, wait.MinutesOffset).ToString();
        for (var pass = 0; pass < 3; pass++)
        {
            night = Night(loaded);
            var current = night.Items.OfType<UvexTargetObservationContainer>().Select(AuditChoices).ToArray();
            Assert.Equal(choices.Length, current.Length);
            foreach (var (expected, actual) in choices.Zip(current))
                Assert.True(expected == actual, $"Saved target settings changed. Expected: {expected}\nActual: {actual}");
            Assert.Equal(targetSettings, night.Items.OfType<UvexTargetObservationContainer>()
                .Select(x => (x.ForceGuideCalibration, x.AutomaticGuideRecovery, x.GuideStarWaitSeconds)));
            Assert.Equal(policy, (night.EndAtIso8601, night.StopAtDawn, night.DawnSunAltitudeDegrees,
                night.EffectiveHoldOpenAfterTargets, night.HomeBeforeParkOnFinalize, night.MountHomeTimeoutSeconds));
            Assert.Empty(NativeNightEndArea.Issues(night));
            var close = Assert.Single(NativeNightEndArea.Choices(night));
            Assert.Same(night, close.Night);
            Assert.False(night.NativeEndInstructionsReady);
            foreach (var target in night.Items.OfType<UvexTargetObservationContainer>())
                Assert.Equal(ObservationRunCoordinator.Stages, target.Items.Cast<ObservationStageMarkerItem>().Select(x => x.Stage));
            if (loaded is SequenceRootContainer root)
            {
                start = Assert.Single(root.Items.OfType<StartAreaContainer>());
                wait = start.Items.OfType<WaitForTime>().SingleOrDefault();
                Assert.Equal(clock, wait is null ? null : (wait.Hours, wait.Minutes, wait.Seconds, wait.MinutesOffset).ToString());
                if (wait is not null)
                    Assert.IsType<NINA.Sequencer.Utility.DateTimeProvider.TimeProvider>(wait.SelectedProvider);
                var prep = Assert.Single(start.Items.OfType<SequentialContainer>());
                Assert.Equal("Safety Monitor", Assert.IsType<ConnectEquipment>(prep.Items[0]).SelectedDevice);
                Assert.IsType<WaitUntilSafe>(prep.Items[1]);
                Assert.Equal(night.Items.OfType<UvexTargetObservationContainer>().All(x => x.UseRealMode) ? 1 : 0,
                    Assert.IsType<LoopCondition>(Assert.Single(prep.Conditions)).Iterations);
                Assert.DoesNotContain(start.Items, x => x is UvexStartSafetyMonitorItem);
                var end = Assert.Single(root.Items.OfType<EndAreaContainer>());
                Assert.Same(end, close.Parent);
                var extras = Assert.Single(end.Items.OfType<SequentialContainer>());
                Assert.IsType<UvexNightClosedCondition>(Assert.Single(extras.Conditions));
                Assert.Empty(extras.Items);
                Assert.DoesNotContain(fixture.Converter.Serialize(loaded), "UnknownSequence");
            }
            else Assert.Same(night, close.Parent); // Reusable block has no root End area.
            loaded = fixture.Converter.Deserialize(fixture.Converter.Serialize(loaded));
        }
        Assert.Equal(0, fixture.TelescopeCalls);
        Assert.Equal(0, fixture.ProfileWritesAfterSetup);
    }

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
            Assert.Equal(3, night.Items.Count);
            Assert.True(Assert.Single(NativeNightEndArea.Choices(night)).CloseAfterTargets);
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
        var preparation = Assert.IsType<SequentialContainer>(start.Items[0]);
        Assert.Equal(0, Assert.IsType<LoopCondition>(Assert.Single(preparation.Conditions)).Iterations);
        Assert.Equal("Safety Monitor", Assert.IsType<ConnectEquipment>(preparation.Items[0]).SelectedDevice);
        Assert.IsType<WaitUntilSafe>(preparation.Items[1]);
        Assert.False(Assert.IsType<UvexSelectRealModeItem>(start.Items[1]).EnableRealMode);
        Assert.All(start.Items.Skip(2), item => Assert.IsType<Annotation>(item));
        Assert.IsType<Annotation>(end.Items[0]);
        Assert.IsType<UvexObservatoryCloseoutItem>(end.Items[1]);
        var extras = Assert.IsType<SequentialContainer>(end.Items[2]);
        Assert.IsType<UvexNightClosedCondition>(Assert.Single(extras.Conditions));
        Assert.Empty(extras.Items);
        Assert.IsType<UvexNightSequenceContainer>(Assert.Single(middle.Items));
        Assert.All(root.Items, item => Assert.Same(root, item.Parent));
        Assert.All(start.Items, item => Assert.Same(start, item.Parent));
        Assert.All(end.Items, item => Assert.Same(end, item.Parent));
    }

    [Fact]
    public void NativeEndChoiceSurvivesSaveLoadAndStandaloneNightCloneWithoutProof()
    {
        using var fixture = new Fixture();
        var loaded = fixture.Load(Full);
        var night = Night(loaded);
        var choice = Assert.Single(NativeNightEndArea.Choices(night));
        Assert.Same(night, choice.Night);
        choice.CloseAfterTargets = false;
        night.HomeBeforeParkOnFinalize = true;
        night.MountHomeTimeoutSeconds = 240;
        for (var pass = 0; pass < 3; pass++)
        {
            loaded = fixture.Converter.Deserialize(fixture.Converter.Serialize(loaded));
            night = Night(loaded);
            Assert.True(night.Validate(), string.Join("; ", night.Issues));
            Assert.True(night.EffectiveHoldOpenAfterTargets);
            Assert.True(night.HomeBeforeParkOnFinalize);
            Assert.Equal(240, night.MountHomeTimeoutSeconds);
            Assert.False(night.NativeEndInstructionsReady);
            Assert.IsType<EndAreaContainer>(Assert.Single(NativeNightEndArea.Choices(night)).Parent);
        }
        var clone = Assert.IsType<UvexNightSequenceContainer>(night.Clone());
        Assert.Null(clone.Parent);
        Assert.True(clone.EffectiveHoldOpenAfterTargets);
        Assert.True(clone.HomeBeforeParkOnFinalize);
        Assert.False(clone.NativeEndInstructionsReady);
        Assert.Empty(NativeNightEndArea.Choices(clone));
        Assert.True(clone.Validate(), string.Join("; ", clone.Issues));
        // NINA's root/area Clone creates an empty factory prototype, not a deep
        // sequence copy. Whole-sequence copies use its save/load converter.
        var rootClone = Assert.IsType<SequenceRootContainer>(
            fixture.Converter.Deserialize(fixture.Converter.Serialize(loaded)));
        var clonedNight = Night(rootClone);
        Assert.True(clonedNight.Validate(), string.Join("; ", clonedNight.Issues));
        Assert.Single(NativeNightEndArea.Choices(clonedNight));
        Assert.DoesNotContain(clonedNight.Items, item => item is UvexObservatoryCloseoutItem);
        Assert.True(clonedNight.EffectiveHoldOpenAfterTargets);
        Assert.False(clonedNight.NativeEndInstructionsReady);
        Assert.Equal(0, fixture.ProfileWritesAfterSetup);
        Assert.Equal(0, fixture.TelescopeCalls);
    }

    [Theory]
    [InlineData("duplicate-choice")]
    [InlineData("unguarded-action")]
    [InlineData("missing-condition")]
    [InlineData("disabled-condition")]
    [InlineData("ambiguous-night")]
    public void NativeEndRejectsAmbiguousChoiceOrUnguardedEquipmentLayout(string layout)
    {
        using var fixture = new Fixture();
        var root = Assert.IsType<SequenceRootContainer>(fixture.Load(Full));
        var night = Night(root);
        var end = Assert.IsType<EndAreaContainer>(root.Items[2]);
        var group = end.Items.OfType<SequentialContainer>().Single();
        switch (layout)
        {
            case "duplicate-choice": night.Add(new UvexObservatoryCloseoutItem()); break;
            case "unguarded-action": end.Add(new EndProbe()); break;
            case "missing-condition": group.Conditions.Clear(); break;
            case "disabled-condition": group.Conditions[0].Status = SequenceEntityStatus.DISABLED; break;
            case "ambiguous-night": ((TargetAreaContainer)root.Items[1]).Add((UvexNightSequenceContainer)night.Clone()); break;
        }
        Assert.False(night.Validate());
        Assert.NotEmpty(night.Issues);
        Assert.False(night.NativeEndInstructionsReady);
        Assert.Equal(0, fixture.TelescopeCalls);
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
            "NINA.Sequencer.Container.SequentialContainer, NINA.Sequencer",
            "NINA.Sequencer.Conditions.LoopCondition, NINA.Sequencer",
            "NINA.Sequencer.SequenceItem.Connect.ConnectEquipment, NINA.Sequencer",
            "NINA.Sequencer.SequenceItem.SafetyMonitor.WaitUntilSafe, NINA.Sequencer",
            "NINA.Sequencer.Container.ExecutionStrategy.SequentialStrategy, NINA.Sequencer",
            "NINA.Sequencer.SequenceItem.Utility.Annotation, NINA.Sequencer",
            "UvexAdv.Nina.Plugin.SequenceItems.UvexNightSequenceContainer, UvexAdv.Nina.Plugin",
            "UvexAdv.Nina.Plugin.SequenceItems.UvexTargetObservationContainer, UvexAdv.Nina.Plugin",
            "UvexAdv.Nina.Plugin.SequenceItems.ObservationStageMarkerItem, UvexAdv.Nina.Plugin",
            "UvexAdv.Nina.Plugin.SequenceItems.SpectroscopyMeridianFlipTrigger, UvexAdv.Nina.Plugin",
            "UvexAdv.Nina.Plugin.SequenceItems.UvexObservatoryCloseoutItem, UvexAdv.Nina.Plugin",
            "UvexAdv.Nina.Plugin.SequenceItems.UvexStartSafetyMonitorItem, UvexAdv.Nina.Plugin",
            "UvexAdv.Nina.Plugin.SequenceItems.UvexSelectRealModeItem, UvexAdv.Nina.Plugin",
            "UvexAdv.Nina.Plugin.SequenceItems.UvexNightClosedCondition, UvexAdv.Nina.Plugin",
        };
        Assert.All(properties.Where(p => p.Name == "$type"), p => Assert.Contains(p.Value.Value<string>(), allowedTypes));
    }

    [Theory]
    [InlineData(Full, false)]
    [InlineData(Full, true)]
    [InlineData(Block, false)]
    public async Task DistributedTemplateRunsThreeTargetsInOrderThroughNativeEngineWithoutHardware(string file, bool endFails)
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
        var endGroup = (loaded as SequenceRootContainer)?.Items.OfType<EndAreaContainer>()
            .Single().Items.OfType<SequentialContainer>().Single();
        var extra = new EndProbe { Fail = endFails };
        endGroup?.Add(extra);
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
        if (endGroup is not null)
        {
            Assert.Equal(1, extra.Calls);
            Assert.Equal(endFails ? SequenceEntityStatus.FAILED : SequenceEntityStatus.FINISHED, extra.Status);
            endGroup.ResetAll();
            await endGroup.Execute(new Progress<ApplicationStatus>(), default);
            Assert.Equal(1, extra.Calls); // Replaying End cannot repeat successful OR partially failed extras.
        }
    }

    private sealed class EndProbe : SequenceItem
    {
        public int Calls;
        public bool Fail;
        public override Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Calls++;
            if (Fail) throw new InvalidOperationException("Fake terminal action failed; no equipment involved.");
            return Task.CompletedTask;
        }
        public override object Clone() => new EndProbe();
    }

    private static void AssertTemplate(ISequenceContainer loaded)
    {
        var night = Night(loaded);
        Assert.Equal(3, night.Items.OfType<UvexTargetObservationContainer>().Count());
        Assert.True(Assert.Single(NativeNightEndArea.Choices(night)).CloseAfterTargets);
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
            Assert.Equal(0, target.FixedExposureSeconds);
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
            NativeAstrometry(); // Every isolated round-trip must have native coordinate transforms, regardless of test order.
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
            var cameraSettings = Proxy<ICameraSettings>((m, _) => m.Name == "get_Id" ? "OFFLINE-CAMERA" : Default(m.ReturnType));
            var safetySettings = Proxy<ISafetyMonitorSettings>((m, _) => m.Name == "get_Id" ? "OFFLINE-SAFETY" : Default(m.ReturnType));
            var monitor = Proxy<ISafetyMonitorMediator>((m, _) =>
            {
                if (m.Name != "GetInfo") throw new InvalidOperationException("Offline template touched monitor: " + m.Name);
                var info = Activator.CreateInstance(m.ReturnType)!;
                m.ReturnType.GetProperty("Connected")!.SetValue(info, true);
                m.ReturnType.GetProperty("IsSafe")!.SetValue(info, true);
                return info;
            });
            var profile = Proxy<IProfile>((m, _) => m.Name switch
            {
                "get_AstrometrySettings" => astrometry, "get_MeridianFlipSettings" => flip,
                "get_CameraSettings" => cameraSettings, "get_SafetyMonitorSettings" => safetySettings,
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
            var time = new NINA.Sequencer.Utility.DateTimeProvider.TimeProvider(Proxy<INighttimeCalculator>());
            var dateTimeProviders = new List<NINA.Sequencer.Utility.DateTimeProvider.IDateTimeProvider> { time };
            var factory = Proxy<ISequencerFactory>((m, _) => m.Name switch
            {
                "get_DateTimeProviders" => dateTimeProviders,
                "GetContainer" => Container(m.GetGenericArguments()[0]),
                "GetItem" when m.GetGenericArguments()[0] == typeof(WaitForTime) => new WaitForTime(dateTimeProviders),
                "GetItem" when m.GetGenericArguments()[0] == typeof(ObservationStageMarkerItem) => new ObservationStageMarkerItem(),
                "GetItem" when m.GetGenericArguments()[0] == typeof(Annotation) => new Annotation(),
                "GetItem" when m.GetGenericArguments()[0] == typeof(ConnectEquipment) =>
                    new ConnectEquipment(profiles, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, monitor),
                "GetItem" when m.GetGenericArguments()[0] == typeof(WaitUntilSafe) => new WaitUntilSafe(monitor),
                "GetCondition" when m.GetGenericArguments()[0] == typeof(LoopCondition) => new LoopCondition(),
                "GetCondition" when m.GetGenericArguments()[0] == typeof(UvexNightClosedCondition) => new UvexNightClosedCondition(),
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
