using System.Reflection;
using System.Runtime.CompilerServices;
using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class ScienceRecoveryBoundaryTests
{
    [Theory]
    [InlineData(ObservationStage.SelectAtrExposure)]
    [InlineData(ObservationStage.RunScienceBlock)]
    public void ActualRunnerAtrInvalidationKeepsPhotometryOwnershipAndFrameCounters(ObservationStage stage)
    {
        // Exercise the production invalidator, without invoking the constructor
        // (which creates real owner clients). This path only invalidates fields;
        // no device object, UI, socket, exposure or service is instantiated.
        var runner = (RealObservationStageRunner)RuntimeHelpers.GetUninitializedObject(typeof(RealObservationStageRunner));
        var job = Guid.NewGuid();
        Set(runner, "photometryJobId", (Guid?)job);
        Set(runner, "qhyPhotometryAttempt", 3);
        Set(runner, "selectedAtrExposureSeconds", (double?)120d);
        Set(runner, "atrReprobeRequired", false);
        Set(runner, "savedAtrFrames", 2);
        Set(runner, "attemptedAtrFrames", 4);
        Set(runner, "retainedAtrScienceFrames", 3);

        typeof(RealObservationStageRunner).GetMethod("InvalidateStageState", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(runner, new object[] { stage });

        Assert.Equal(job, Get<Guid?>(runner, "photometryJobId"));
        Assert.Equal(3, Get<int>(runner, "qhyPhotometryAttempt"));
        Assert.Null(Get<double?>(runner, "selectedAtrExposureSeconds"));
        Assert.True(Get<bool>(runner, "atrReprobeRequired"));
        Assert.Equal(2, Get<int>(runner, "savedAtrFrames"));
        Assert.Equal(4, Get<int>(runner, "attemptedAtrFrames"));
        Assert.Equal(3, Get<int>(runner, "retainedAtrScienceFrames"));
    }

    [Fact]
    public void RejectedGuideFrameIsSavedBeforeBoundedRecoveryAndNeverAccepted()
    {
        var science = Slice("private async Task<StageResult> RunScienceBlockAsync(",
            "private async Task<StageResult> FinalizeObservationAsync(");
        var save = science.IndexOf("await SaveAtrImageAsync(", StringComparison.Ordinal);
        var retained = science.IndexOf("retainedAtrScienceFrames++;", StringComparison.Ordinal);
        var rejection = science.IndexOf("if (quality.Code == \"GUIDING_UNSTABLE\")", StringComparison.Ordinal);
        var recovery = science.IndexOf("return new StageResult(quality);", rejection, StringComparison.Ordinal);
        var accepted = science.IndexOf("savedAtrFrames++;", StringComparison.Ordinal);
        Assert.True(save >= 0 && retained > save && rejection > retained && recovery > rejection && accepted > recovery);
        Assert.Contains("quality.Disposition == GateDisposition.Passed", science, StringComparison.Ordinal);
        Assert.Contains("AtrScienceFrameQualityPolicy.Evaluate(metrics, configuration.Atr)", Source, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadOnlyRecoveryDoesNotSwallowOperatorRestartOrPhysicalEvidenceGates()
    {
        var recovery = Slice("private async Task<(bool Retry, StageResult Result)> PrepareAutomaticStageRecoveryAsync(",
            "private async Task<GateResult> EnsurePhdStoppedForAutomaticRebuildAsync(");
        Assert.Contains("catch (ResumeStageRestartException) { throw; }", recovery, StringComparison.Ordinal);
        Assert.Contains("catch (PhysicalActionGateException ex)", recovery, StringComparison.Ordinal);
        Assert.Contains("return (false, new StageResult(ex.Gate));", recovery, StringComparison.Ordinal);
        Assert.Contains("PHD2_SCIENCE_IN_PLACE_CHECK_FAILED", recovery, StringComparison.Ordinal);
        var physicalCatch = Slice("catch (PhysicalActionGateException ex)\n", "catch (OperationCanceledException)\n");
        Assert.Contains("catch (ResumeStageRestartException)", physicalCatch, StringComparison.Ordinal);
        Assert.Contains("continue;", physicalCatch, StringComparison.Ordinal);
    }

    [Fact]
    public void ScienceBudgetIsCheckedBeforeReprobeAndAfterRejectionBeforeRecovery()
    {
        var science = Slice("private async Task<StageResult> RunScienceBlockAsync(",
            "private async Task<StageResult> FinalizeObservationAsync(");
        var entry = science.IndexOf("RemainingAttemptGate(", StringComparison.Ordinal);
        var probe = science.IndexOf("if (atrReprobeRequired)", StringComparison.Ordinal);
        var loop = science.IndexOf("while (savedAtrFrames", StringComparison.Ordinal);
        var loopGate = science.IndexOf("RemainingAttemptGate(", loop, StringComparison.Ordinal);
        var loopProbe = science.IndexOf("if (atrReprobeRequired)", loop, StringComparison.Ordinal);
        var exhausted = science.IndexOf("var exhaustedAttempts", StringComparison.Ordinal);
        var guideRecovery = science.IndexOf("if (quality.Code == \"GUIDING_UNSTABLE\")", StringComparison.Ordinal);
        Assert.True(entry >= 0 && entry < probe && loopGate > loop && loopGate < loopProbe);
        Assert.True(exhausted > loopProbe && exhausted < guideRecovery);
    }

    [Fact]
    public void TerminalScienceAttemptsStillReconcileDebtButNeverEnterResumeOrAutomaticRebuild()
    {
        var execute = Slice("public override async Task<StageResult> ExecuteStageAsync(",
            "private async Task<(bool Retry, StageResult Result)> PrepareAutomaticStageRecoveryAsync(");
        var debt = execute.IndexOf("await RecoverDurableSlitPlacementBeforeStageAsync(", StringComparison.Ordinal);
        var terminal = execute.IndexOf("RemainingAttemptGate(", StringComparison.Ordinal);
        var resume = execute.IndexOf("await RecoverInterruptedStageAsync(", StringComparison.Ordinal);
        Assert.True(debt >= 0 && terminal > debt && resume > terminal);
        var automatic = Slice("private async Task<(bool Retry, StageResult Result)> PrepareAutomaticStageRecoveryAsync(",
            "private async Task<GateResult> EnsurePhdStoppedForAutomaticRebuildAsync(");
        Assert.True(automatic.IndexOf("RemainingAttemptGate(", StringComparison.Ordinal) <
            automatic.IndexOf("switch (plan.Action)", StringComparison.Ordinal));
    }

    private static readonly string Source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "RealObservationStageRunner.cs")).Replace("\r\n", "\n");
    private static string Slice(string start, string end)
    {
        var from = Source.IndexOf(start, StringComparison.Ordinal);
        var to = Source.IndexOf(end, from + start.Length, StringComparison.Ordinal);
        Assert.True(from >= 0 && to > from);
        return Source[from..to];
    }

    private static void Set(object instance, string name, object? value) => Field(name).SetValue(instance, value);
    private static T? Get<T>(object instance, string name) => (T?)Field(name).GetValue(instance);
    private static FieldInfo Field(string name) => typeof(RealObservationStageRunner).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
}
