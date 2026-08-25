using NINA.Sequencer.Container;
using UvexAdv.Nina.Plugin.SequenceItems;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class NinaImageFilePatternPolicyTests
{
    [Fact]
    public void RecommendedPatternIsCompliantAndTargetSeparated()
    {
        var assessment = NinaImageFilePatternPolicy.Assess(
            NinaImageFilePatternPolicy.RecommendedPattern);

        Assert.True(assessment.IsCompliant);
        Assert.True(assessment.UsesRecommendedPattern);
        Assert.Empty(assessment.BlockingIssues);
        Assert.Empty(assessment.Recommendations);
        Assert.Contains("$$TARGETNAME$$\\$$IMAGETYPE$$", assessment.CurrentPattern, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingTargetTokenBlocksButAlternativeLayoutDoesNot()
    {
        var missingTarget = NinaImageFilePatternPolicy.Assess(
            "$$DATEMINUS12$$\\$$IMAGETYPE$$\\$$DATETIME$$");
        var alternative = NinaImageFilePatternPolicy.Assess(
            "$$TARGETNAME$$\\science\\$$DATETIME$$");

        Assert.False(missingTarget.IsCompliant);
        Assert.Contains(missingTarget.BlockingIssues, issue =>
            issue.Contains(NinaImageFilePatternPolicy.TargetToken, StringComparison.Ordinal));
        Assert.True(alternative.IsCompliant);
        Assert.Single(alternative.Recommendations);
        Assert.False(alternative.UsesRecommendedPattern);
    }

    [Fact]
    public void TargetObservationContainerPublishesNinaNativeTargetContract()
    {
        Assert.True(typeof(IDeepSkyObjectContainer).IsAssignableFrom(
            typeof(UvexTargetObservationContainer)));
        Assert.Equal(
            typeof(NINA.Astrometry.InputTarget),
            typeof(UvexTargetObservationContainer).GetProperty(nameof(UvexTargetObservationContainer.Target))!.PropertyType);
        Assert.Equal(
            typeof(NINA.Astrometry.InputTarget),
            typeof(ObservationDockable).GetProperty(nameof(ObservationDockable.Target))!.PropertyType);
    }
}
