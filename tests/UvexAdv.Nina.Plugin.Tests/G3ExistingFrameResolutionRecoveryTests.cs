using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class G3ExistingFrameResolutionRecoveryTests
{
    private sealed record Result(string Source, G3CompletedSolveResolutionResult State);

    [Theory]
    [InlineData("NoSolution")]
    [InlineData("Implausible")]
    public async Task OutsideHandoffReusesExactlyTheSameFrameAtRawResolutionAfterCompletedBinTwoRejection(
        string firstResult)
    {
        var firstState = Enum.Parse<G3CompletedSolveResolutionResult>(firstResult);
        var calls = new List<string>();
        var source = "immutable-existing-frame.fit";
        var result = await G3ExistingFrameResolutionRecovery.SolveAsync(true, 2, null,
            (factor, _) =>
            {
                calls.Add($"solve:{factor?.ToString() ?? "default-bin2"}:{source}");
                return Task.FromResult(new Result(source, factor is null ? firstState : G3CompletedSolveResolutionResult.Accepted));
            }, first => first.State,
            (first, _) => { calls.Add($"recheck:{first.Source}"); return Task.CompletedTask; },
            CancellationToken.None);
        Assert.Equal(G3CompletedSolveResolutionResult.Accepted, result.State);
        Assert.Equal(source, result.Source);
        Assert.Equal(new[] { $"solve:default-bin2:{source}", $"recheck:{source}", $"solve:1:{source}" }, calls);
    }

    [Theory]
    [InlineData(false, 2, "NoSolution")]
    [InlineData(true, 1, "NoSolution")]
    [InlineData(true, 3, "NoSolution")]
    [InlineData(true, 2, "Accepted")]
    [InlineData(true, 2, "ParityMismatch")]
    public async Task OrdinaryLadderOtherFactorsSuccessAndParityMismatchNeverGainAnExtraInvocation(
        bool outside, int factor, string completedResult)
    {
        var state = Enum.Parse<G3CompletedSolveResolutionResult>(completedResult);
        var solves = 0;
        var checks = 0;
        var first = new Result("source.fit", state);
        var result = await G3ExistingFrameResolutionRecovery.SolveAsync(outside, factor, null,
            (_, _) => { solves++; return Task.FromResult(first); }, item => item.State,
            (_, _) => { checks++; return Task.CompletedTask; }, CancellationToken.None);
        Assert.Same(first, result);
        Assert.Equal(1, solves);
        Assert.Equal(0, checks);
    }

    [Fact]
    public async Task AlreadyNativeResolutionIsNotRetriedEvenIfCallerReportsFactorTwo()
    {
        var factors = new List<int?>();
        await G3ExistingFrameResolutionRecovery.SolveAsync(true, 2, 1,
            (factor, _) => { factors.Add(factor); return Task.FromResult(new Result("source.fit", G3CompletedSolveResolutionResult.NoSolution)); },
            item => item.State, (_, _) => throw new InvalidOperationException("Unexpected recheck"), CancellationToken.None);
        Assert.Equal(new int?[] { 1 }, factors);
    }

    [Fact]
    public async Task RawFailureIsReturnedAfterOneRecoveryNotAnotherResolutionLoop()
    {
        var solves = 0;
        var result = await G3ExistingFrameResolutionRecovery.SolveAsync(true, 2, null,
            (_, _) => { solves++; return Task.FromResult(new Result("source.fit", G3CompletedSolveResolutionResult.NoSolution)); },
            item => item.State, (_, _) => Task.CompletedTask, CancellationToken.None);
        Assert.Equal(2, solves);
        Assert.Equal(G3CompletedSolveResolutionResult.NoSolution, result.State);
    }

    [Fact]
    public async Task ProcessExceptionDoesNotBecomeAnotherSolverInvocation()
    {
        var solves = 0;
        var failure = new IOException("PL3 process or source failed");
        var actual = await Assert.ThrowsAsync<IOException>(() => G3ExistingFrameResolutionRecovery.SolveAsync<Result>(true, 2, null,
            (_, _) => { solves++; throw failure; }, item => item.State,
            (_, _) => throw new InvalidOperationException("Unexpected recheck"), CancellationToken.None));
        Assert.Same(failure, actual);
        Assert.Equal(1, solves);
    }

    [Theory]
    [InlineData("ENVIRONMENT_SAFETY_TRIPPED")]
    [InlineData("PHD2_IDENTITY_STALE")]
    [InlineData("G3_FIELD_MOUNT_BINDING_MISMATCH")]
    public async Task LateUnsafeOwnerOrBindingFailurePropagatesAndPreventsRawSolve(string code)
    {
        var solves = 0;
        var failure = new PhysicalActionGateException(GateResult.Unknown(code, "changed boundary"));
        var actual = await Assert.ThrowsAsync<PhysicalActionGateException>(() =>
            G3ExistingFrameResolutionRecovery.SolveAsync(true, 2, null,
                (_, _) => { solves++; return Task.FromResult(new Result("source.fit", G3CompletedSolveResolutionResult.NoSolution)); },
                item => item.State, (_, _) => throw failure, CancellationToken.None));
        Assert.Same(failure, actual);
        Assert.Equal(1, solves);
    }

    [Fact]
    public async Task CancellationBetweenCompletedFirstResultAndRecoveryStopsWithoutSecondSolve()
    {
        using var cancelled = new CancellationTokenSource();
        var solves = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => G3ExistingFrameResolutionRecovery.SolveAsync(true, 2, null,
            (_, _) =>
            {
                solves++;
                cancelled.Cancel();
                return Task.FromResult(new Result("source.fit", G3CompletedSolveResolutionResult.NoSolution));
            }, item => item.State, (_, _) => throw new InvalidOperationException("Unexpected recheck"), cancelled.Token));
        Assert.Equal(1, solves);
    }

    [Fact]
    public async Task OriginalReturnTimePolicyCanRejectSecondInvocationBeforeTheSolverRuns()
    {
        var started = new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
        var remaining = new G3AcquisitionWorkTimePlan(GateResult.Pass("ready", "original reserve"), TimeSpan.FromSeconds(1));
        var policyChecks = 0;
        var nativeSolves = 0;
        await Assert.ThrowsAsync<G3ReturnTimeReserveException>(() => G3ExistingFrameResolutionRecovery.SolveAsync(true, 2, null,
            (factor, token) =>
            {
                policyChecks++;
                // Represents the production SolveImageAsync guard re-evaluated
                // from the SAME ledger clock after the first solve spent time.
                var plan = policyChecks == 1 ? remaining : new G3AcquisitionWorkTimePlan(
                    GateResult.TargetQuality(G3AcquisitionReturnTimePolicy.ReserveCode, $"Original deadline {started:o} remains unchanged."), null);
                return G3AcquisitionReturnTimePolicy.ExecuteAsync(plan, _ =>
                {
                    nativeSolves++;
                    return Task.FromResult(new Result("source.fit", G3CompletedSolveResolutionResult.NoSolution));
                }, token);
            }, item => item.State, (_, _) => Task.CompletedTask, CancellationToken.None));
        Assert.Equal(2, policyChecks);
        Assert.Equal(1, nativeSolves);
    }
}
