using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class G3SolvedOriginRecoveryTests
{
    private static readonly GateResult FailedLadder = GateResult.Unknown("G3_PLATE_SOLVE_LADDER_EXHAUSTED_STRUCTURED_FIELD", "completed failure");
    private static readonly GateResult Outside = GateResult.Unknown("G3_SOLVED_TARGET_OUTSIDE", "new formal solve");

    [Fact]
    public void NewFormalOriginSolveUsesNormalWcsRouteInsteadOfThrowingItAwayForBlindSearch()
    {
        Assert.True(G3WcsRecoveryPolicy.PreferSolvedOriginToBlindSearch(1, FailedLadder, Outside, true));
        Assert.True(G3WcsRecoveryPolicy.PreferSolvedOriginToBlindSearch(1, FailedLadder,
            GateResult.Pass("G3_FIELD_ANALYZED_RUN_SLIT_GEOMETRY", "new solved in-frame field"), true));
    }

    [Fact]
    public void ZeroActionFailureUnsolvedFrameOrUntrustedGateCannotTakeTheRecursiveShortcut()
    {
        Assert.False(G3WcsRecoveryPolicy.PreferSolvedOriginToBlindSearch(0, FailedLadder, Outside, true));
        Assert.False(G3WcsRecoveryPolicy.PreferSolvedOriginToBlindSearch(1, FailedLadder, Outside, false));
        Assert.False(G3WcsRecoveryPolicy.PreferSolvedOriginToBlindSearch(1, FailedLadder,
            GateResult.Unknown("G3_PLATE_SOLVE_PLAUSIBILITY_REJECTED", "not trusted"), true));
        Assert.False(G3WcsRecoveryPolicy.PreferSolvedOriginToBlindSearch(1, FailedLadder,
            GateResult.Fail("G3_SOLVED_TARGET_OUTSIDE", "blocked"), true));
        Assert.False(G3WcsRecoveryPolicy.PreferSolvedOriginToBlindSearch(1,
            GateResult.Unknown("G3_MOTION_RETURN_TIME_RESERVE_LIMIT", "budget exhausted"), Outside, true));
        Assert.False(G3WcsRecoveryPolicy.PreferSolvedOriginToBlindSearch(1, null, Outside, true));
        Assert.False(G3WcsRecoveryPolicy.PreferSolvedOriginToBlindSearch(1, FailedLadder, null, true));
    }

    [Fact]
    public void CaptureAndSolveReturnFenceFlowsIntoBothExistingGuardedReturnBranches()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "RealObservationStageRunner.cs"));
        Assert.Contains("RunWithG3PendingReturnTimeAsync(\"capture\"", source);
        Assert.Contains("token => CaptureG3NativeSingleFrameCoreAsync(request, token)", source);
        Assert.Contains("RunWithG3PendingReturnTimeAsync(\"solve\"", source);
        Assert.Contains("role.StartsWith(\"PHD2/G3\", StringComparison.Ordinal)", source);
        Assert.Equal(2, source.Split("catch (G3ReturnTimeReserveException ex) when (ex.Gate.Code == G3AcquisitionReturnTimePolicy.ReserveCode)").Length - 1);
        Assert.Contains("localSearchBudgetExhausted = solveStoppedToPreserveReturn ||", source);
        Assert.Contains("ReturnDurableG3AcquisitionToOriginAsync(context, state, cancellationToken)", source);
    }

    [Fact]
    public void RunnerUsesOnlyTheNewPostReturnFrameAndTheNormalGuardedWcsMethod()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "RealObservationStageRunner.cs"));
        var start = source.IndexOf("var originField = await CaptureAndAnalyzeG3WithSolveLadderAsync", StringComparison.Ordinal);
        var end = source.IndexOf("if (originField.Gate.Disposition == GateDisposition.Passed)", start, StringComparison.Ordinal);
        var shortcut = source[start..end];
        Assert.Contains("PreferSolvedOriginToBlindSearch", shortcut);
        Assert.Contains("originField.Solve?.Result.Success == true && originField.Solve.Result.Coordinates is not null", shortcut);
        Assert.Contains("RunG3WcsCenteringAsync(context, originField, transferEvidencePath", shortcut);
        Assert.DoesNotContain("SlewToCoordinatesAsync", shortcut);
        Assert.DoesNotContain("G3FieldPassed(", shortcut);
        Assert.DoesNotContain("CorrectionAttempts = 0", shortcut);
        Assert.Contains("budgetReset = false", shortcut);
    }
}
