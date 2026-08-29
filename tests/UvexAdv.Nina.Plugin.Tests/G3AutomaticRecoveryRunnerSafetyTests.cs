using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class G3AutomaticRecoveryRunnerSafetyTests
{
    private static readonly string MainSource = File.ReadAllText(Path.Combine(
        AppContext.BaseDirectory,
        "Sources",
        "RealObservationStageRunner.cs"));

    [Fact]
    public void AutomaticRecoveryAttemptBudgetSurvivesTheRetryLoop()
    {
        var runStage = Slice(
            MainSource,
            "public override async Task<StageResult> ExecuteStageAsync(",
            "private async Task<(bool Retry, StageResult Result)> PrepareAutomaticStageRecoveryAsync(");
        var session = runStage.IndexOf(
            "var automaticRecoverySession = new ObservationAutomaticRecoverySession();",
            StringComparison.Ordinal);
        var retryLoop = runStage.IndexOf("while (true)", StringComparison.Ordinal);

        Assert.True(session >= 0 && retryLoop > session);
        Assert.Equal(1, Count(runStage, "new ObservationAutomaticRecoverySession()"));
    }

    [Fact]
    public void FreshEvidenceRetryCannotResetDurableG3MotionConsumption()
    {
        var recovery = Slice(
            MainSource,
            "private async Task<(bool Retry, StageResult Result)> PrepareAutomaticStageRecoveryAsync(",
            "public override async Task<GateResult> RevalidateAsync(");
        var invalidation = Slice(
            MainSource,
            "private void InvalidateStageState(ObservationStage stage)",
            "private void UpdateRemainingScienceDuration(");

        Assert.Contains("session.Evaluate(stage, result.Gate)", recovery, StringComparison.Ordinal);
        Assert.Contains("decision.Exhausted", recovery, StringComparison.Ordinal);
        Assert.Contains("ObservationAutomaticRecoverySession.MaximumTotalAttempts", recovery, StringComparison.Ordinal);
        Assert.Contains("durableMotionBudgetsReset = false", recovery, StringComparison.Ordinal);
        Assert.DoesNotContain("durableG3AcquisitionMotion = null", recovery, StringComparison.Ordinal);
        Assert.DoesNotContain("cumulativeCorrectionDegrees = 0", recovery, StringComparison.Ordinal);
        Assert.DoesNotContain("correctionAttempts = 0", recovery, StringComparison.Ordinal);
        Assert.Contains("lastG3Field = null", invalidation, StringComparison.Ordinal);
        Assert.DoesNotContain("durableG3AcquisitionMotion", invalidation, StringComparison.Ordinal);
        Assert.DoesNotContain("cumulativeCorrectionDegrees", invalidation, StringComparison.Ordinal);
        Assert.DoesNotContain("correctionAttempts", invalidation, StringComparison.Ordinal);
    }

    private static int Count(string source, string value)
    {
        var count = 0;
        var offset = 0;
        while ((offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }
        return count;
    }

    private static string Slice(string source, string start, string end)
    {
        var from = source.IndexOf(start, StringComparison.Ordinal);
        var to = source.IndexOf(end, from + start.Length, StringComparison.Ordinal);
        Assert.True(from >= 0 && to > from);
        return source[from..to];
    }
}
