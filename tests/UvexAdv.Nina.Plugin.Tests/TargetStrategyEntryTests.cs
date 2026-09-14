using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class TargetStrategyEntryTests
{
    [Fact]
    public void BothEntriesUseSameImmutableMetadataAndPriority()
    {
        var metadata = new TargetCatalogMetadata("M76", "NGC 650", 25.582, 51.575,
            "Stellarium", "Planetary nebula", 10.1, DateTimeOffset.UtcNow);
        var dockable = Create(metadata);
        var sequencer = Create(TargetCatalogMetadataSerialization.Read(TargetCatalogMetadataSerialization.Write(metadata)));
        var first = TargetAcquisitionStrategyPolicy.Resolve(dockable.TargetObservability, dockable.Target, dockable.CatalogMetadata);
        var second = TargetAcquisitionStrategyPolicy.Resolve(sequencer.TargetObservability, sequencer.Target, sequencer.CatalogMetadata);
        Assert.Equal(TargetObservabilityClass.CompactExtended, first.EffectiveClass);
        Assert.Equal(first.Summary, second.Summary);
        Assert.Equal(first.OrderedBranches, second.OrderedBranches);
        Assert.Equal(dockable.Motion, sequencer.Motion);
        Assert.Equal(11, ObservationRunCoordinator.Stages.Count);
    }

    [Fact]
    public void SerializedMetadataDoesNotFollowDifferentSequencerTarget()
    {
        var metadata = new TargetCatalogMetadata("M76", "NGC 650", 25.582, 51.575,
            "Stellarium", "Planetary nebula", 10.1, DateTimeOffset.UtcNow);
        var plan = Create(metadata) with { Target = new("Other", "", 30, 40) };
        var decision = TargetAcquisitionStrategyPolicy.Resolve(plan.TargetObservability, plan.Target, plan.CatalogMetadata);
        Assert.False(decision.UsesCatalogPositionOnly);
        Assert.Contains("不匹配", decision.Summary);
    }

    [Fact]
    public void VisibleAutomaticChoiceIsLastAndManualValuesAreStable()
    {
        var choices = ObservationDockable.TargetObservabilityChoices;
        Assert.Equal(TargetObservabilityClass.AutoFromPlanetarium, choices[^1].Value);
        Assert.Equal("根据星图自动", choices[^1].Label);
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, choices.Select(x => (int)x.Value));
        Assert.DoesNotContain(choices, x => x.Description.Contains("SNR 优先"));
    }

    private static ObservationPlan Create(TargetCatalogMetadata? metadata) => ObservationPlanFactory.Create(
        "M76", "NGC 650", 25.582, 51.575, 20, "test-night", 33, 120, 0,
        30, 2, 2, "spectroscopy-camera", "guide-profile", "photometry-camera",
        new MotionLimits(), false, TargetObservabilityClass.AutoFromPlanetarium, metadata);
}
