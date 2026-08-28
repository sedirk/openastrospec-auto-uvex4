using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class ProductionRouteParitySourceTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string DockableSource = ReadSource("ObservationDockable.cs");
    private static readonly string SequenceSource = ReadSource(
        "SequenceItems",
        "UvexTargetObservationContainer.cs");

    [Fact]
    public void DockableRealStartUsesTheSharedLockedProductionRoute()
    {
        var body = MethodBody(
            DockableSource,
            "private async Task StartRealAsync()",
            "private void Resume()");

        AssertOrdered(
            body,
            "realRunnerFactory.CaptureConfiguration(settings)",
            "ObservationPlanFactory.FromSettings(settings, lockedConfiguration)",
            "realRunnerFactory.Create(",
            "host.RunAsync(plan, runner");
        Assert.DoesNotContain("new RealObservationStageRunner", body, StringComparison.Ordinal);
    }

    [Fact]
    public void AdvancedSequencerUsesTheSameFactoryCoordinatorAndCanonicalStages()
    {
        var execute = MethodBody(
            SequenceSource,
            "public override async Task Execute(",
            "public override Task Interrupt()");
        var buildPlan = MethodBody(
            SequenceSource,
            "private ObservationPlan BuildPlan(",
            "private void LoadDefaults()");

        AssertOrdered(
            execute,
            "realRunnerFactory.CaptureConfiguration(settings)",
            "BuildPlan(lockedConfiguration)",
            "realRunnerFactory.Create(",
            "host.RunAsync(plan, bridge");
        Assert.Contains("ObservationPlanFactory.Create(", buildPlan, StringComparison.Ordinal);
        Assert.DoesNotContain("new ObservationPlan", buildPlan, StringComparison.Ordinal);
        Assert.DoesNotContain("new RealObservationStageRunner", execute, StringComparison.Ordinal);
        Assert.Contains("ObservationRunCoordinator.Stages", SequenceSource, StringComparison.Ordinal);

        Assert.Equal(
            new[]
            {
                ObservationStage.ValidateNightSetup,
                ObservationStage.SlewToCatalogTarget,
                ObservationStage.AcquireQhyWideField,
                ObservationStage.CoarseCenter,
                ObservationStage.AcquireG3SlitField,
                ObservationStage.PlaceTargetOnSlit,
                ObservationStage.StartGuiding,
                ObservationStage.StartQhyPhotometry,
                ObservationStage.SelectAtrExposure,
                ObservationStage.RunScienceBlock,
                ObservationStage.FinalizeObservation,
            },
            ObservationRunCoordinator.Stages);
    }

    private static void AssertOrdered(string source, params string[] markers)
    {
        var prior = -1;
        foreach (var marker in markers)
        {
            var index = source.IndexOf(marker, StringComparison.Ordinal);
            Assert.True(index > prior, $"Expected '{marker}' after the preceding production-route marker.");
            prior = index;
        }
    }

    private static string MethodBody(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        var end = source.IndexOf(endMarker, start + 1, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, $"Could not isolate {startMarker}.");
        return source[start..end];
    }

    private static string ReadSource(params string[] parts) =>
        File.ReadAllText(Path.Combine(
            new[] { RepositoryRoot, "src", "UvexAdv.Nina.Plugin" }.Concat(parts).ToArray()));

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "UVEX-ADV.sln"))) return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Repository root containing UVEX-ADV.sln was not found.");
    }
}
