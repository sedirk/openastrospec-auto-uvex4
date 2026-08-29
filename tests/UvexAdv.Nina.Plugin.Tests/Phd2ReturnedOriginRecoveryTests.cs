using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class Phd2ReturnedOriginRecoveryTests
{
    private static readonly string Source = File.ReadAllText(Path.Combine(
        FindRepositoryRoot(),
        "src",
        "UvexAdv.Nina.Plugin",
        "RealObservationStageRunner.Phd2SlitPlacement.cs"));

    [Fact]
    public void ReturnedOriginRetriesOnlyCheckedStopAndNeverMovement()
    {
        var body = Slice(
            Source,
            "private async Task<Exception?> StopPhdAfterOriginReachedWithRetryAsync()",
            "private Task<StageResult> ReturnPhd2LockToOriginAsync(");

        Assert.Contains("const int maximumAttempts = 2", body, StringComparison.Ordinal);
        Assert.Contains("StopPhdAndWaitAsync(CancellationToken.None)", body, StringComparison.Ordinal);
        Assert.Contains("仅重试一次幂等停止与读回", body, StringComparison.Ordinal);
        Assert.DoesNotContain("SetLockPosition", body, StringComparison.Ordinal);
        Assert.DoesNotContain("StartGuiding", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Slew", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Move", body, StringComparison.Ordinal);
    }

    [Fact]
    public void DependencyRebuildCodeRequiresFreshOriginAndSuccessfulCheckedStop()
    {
        var section = Slice(
            Source,
            "var plan = Phd2SlitLockShiftPlanner.PlanRecoveryStage(",
            "var stage = plan.Stage!;");

        AssertOrdered(
            section,
            "if (finalVerification is not null)",
            "finalVerification(actual",
            "Phd2LockShiftPendingStore.WriteAtomicAsync(path, settledState",
            "StopPhdAfterOriginReachedWithRetryAsync()",
            "PHD2_LOCK_FAILURE_RETURNED");
    }

    private static void AssertOrdered(string source, params string[] markers)
    {
        var previous = -1;
        foreach (var marker in markers)
        {
            var index = source.IndexOf(marker, previous + 1, StringComparison.Ordinal);
            Assert.True(index > previous, $"Expected '{marker}' after index {previous}.");
            previous = index;
        }
    }

    private static string Slice(string source, string start, string end)
    {
        var startIndex = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(startIndex >= 0, $"Start marker not found: {start}");
        var endIndex = source.IndexOf(end, startIndex + start.Length, StringComparison.Ordinal);
        Assert.True(endIndex > startIndex, $"End marker not found after start: {end}");
        return source[startIndex..endIndex];
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src")) &&
                File.Exists(Path.Combine(directory.FullName, "UVEX-ADV.sln")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
