using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class G3NeighbourRecoverySourceTests
{
    [Fact]
    public void FailedNeighbourIsPersistedAndRetainedAcrossRecoveryBeforeItCanBeSelectedAgain()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "RealObservationStageRunner.cs"));
        var start = source.IndexOf("private async Task<StageResult> RunG3WcsCenteringAsync(", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var method = source[start..];
        Assert.Contains("var failedNeighbourApproachesBefore = state.FailedNeighbourApproaches", method);
        Assert.Contains("currentField.Image.Properties.Height,\n                failedNeighbourApproachesBefore)", method.Replace("\r\n", "\n"));
        var completedFailure = method.IndexOf("if (isSolvedNeighbourApproach && currentField.Solve?.Result.Success != true", StringComparison.Ordinal);
        Assert.True(completedFailure > 0);
        var failureEnd = method.IndexOf("var currentResidual =", completedFailure, StringComparison.Ordinal);
        var failure = method[completedFailure..failureEnd];
        Assert.Contains("G3_PLATE_SOLVE_LADDER_EXHAUSTED", failure);
        Assert.Contains("FailedNeighbourApproaches = checked(state.FailedNeighbourApproaches + 1)", failure);
        Assert.Contains("await PersistG3AcquisitionMotionAsync(state, CancellationToken.None)", failure);
        Assert.DoesNotContain("CorrectionAttempts =", failure);
        Assert.DoesNotContain("CumulativeMotionArcseconds =", failure);
        Assert.DoesNotContain("StartedUtc =", failure);
        Assert.Contains("FailedNeighbourApproaches = lineageCopies.Max(copy => copy.FailedNeighbourApproaches)", source);
    }
}
