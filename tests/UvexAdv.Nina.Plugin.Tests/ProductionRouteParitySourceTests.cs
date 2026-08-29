using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class ProductionRouteParitySourceTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string DockableSource = ReadSource("ObservationDockable.cs");
    private static readonly string PresentationSource = ReadSource("ObservationUiPresentation.cs");
    private static readonly string RealRunnerSource = ReadSource("RealObservationStageRunner.cs");
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

    [Fact]
    public void DeliberateRouteChangeRetiresTheOldBoundaryBeforeStartingAFreshRun()
    {
        var body = MethodBody(
            DockableSource,
            "private async Task RestartWithCurrentConfigurationAsync()",
            "private async Task<bool> RetireG3RecoveryStateAsync()");

        AssertOrdered(
            body,
            "RealModeEligibilityIssues()",
            "RetireG3RecoveryStateAsync()",
            "StartRealAsync()");
        Assert.Contains("重新捕获动作配置哈希", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ActionConfigurationSha256 =", body, StringComparison.Ordinal);
    }

    [Fact]
    public void OptionalQhyFailuresDegradeWithoutStoppingSpectroscopy()
    {
        var staticValidation = MethodBody(
            RealRunnerSource,
            "private IReadOnlyList<string> ValidateStaticConfiguration(ObservationPlan plan)",
            "private Phd2IdentityRequirement PhdIdentityRequirement()");

        Assert.Contains("AcquireOptionalQhyWideFieldAsync", RealRunnerSource, StringComparison.Ordinal);
        Assert.Contains("StartOptionalQhyPhotometryAsync", RealRunnerSource, StringComparison.Ordinal);
        Assert.Contains("RestorePhotometryAfterResumeAsync", RealRunnerSource, StringComparison.Ordinal);
        Assert.Contains("ValidateOptionalQhyConfiguration", RealRunnerSource, StringComparison.Ordinal);
        Assert.Contains("PauseOnQualityFailure: false", RealRunnerSource, StringComparison.Ordinal);
        Assert.Contains("QHY_WIDE_FIELD_DEGRADED_TO_G3", RealRunnerSource, StringComparison.Ordinal);
        Assert.Contains("QHY_PHOTOMETRY_DEGRADED", RealRunnerSource, StringComparison.Ordinal);
        Assert.Contains("OBSERVATION_FINALIZED_WITH_WARNINGS", RealRunnerSource, StringComparison.Ordinal);
        Assert.DoesNotContain("A real QHY StableId is required", staticValidation, StringComparison.Ordinal);
        Assert.DoesNotContain("Measured GS350/QHY focal length", staticValidation, StringComparison.Ordinal);
        Assert.DoesNotContain("QHY exposure ladder is empty", staticValidation, StringComparison.Ordinal);
        Assert.DoesNotContain("QHY service URL must", staticValidation, StringComparison.Ordinal);
    }

    [Fact]
    public void LowSignalAndLocalMorphologyDoNotPreemptBoundedRecovery()
    {
        Assert.Contains("ATR_SIGNAL_LIMITED_LONGEST_SAFE_TIER", RealRunnerSource, StringComparison.Ordinal);
        Assert.Contains("ATR_TARGET_CONTRAST_LOW", RealRunnerSource, StringComparison.Ordinal);
        Assert.Contains("ATR_SNR_LOW", RealRunnerSource, StringComparison.Ordinal);
        Assert.Contains("G3_PLATE_SOLVE_LADDER_EXHAUSTED_ENVIRONMENT_ATTESTED_FIELD", RealRunnerSource, StringComparison.Ordinal);
        Assert.Contains("configuration.Environment.WeakSupervisionEnabled", RealRunnerSource, StringComparison.Ordinal);
        Assert.Contains("IsRecoverableG3SearchGate", RealRunnerSource, StringComparison.Ordinal);
    }

    [Fact]
    public void OperatorUiNamesWarningsAsContinuingAndErrorsAsPaused()
    {
        Assert.Contains("警告后继续", PresentationSource, StringComparison.Ordinal);
        Assert.Contains("未通过，已暂停", PresentationSource, StringComparison.Ordinal);
        Assert.Contains("证据不足，已暂停", PresentationSource, StringComparison.Ordinal);
        Assert.Contains("Warning; continued", PresentationSource, StringComparison.Ordinal);
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
