using NINA.Astrometry;
using NINA.PlateSolving;
using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class G3SparseProvenanceTests
{
    [Fact]
    public void RegisteredMosaicAnchorCannotLoseItsOriginalUncertainty()
    {
        Assert.Equal(28, RealObservationStageRunner.RegisteredSparseUncertainty(20, 1.1, 2, 4));
        Assert.Equal(9, RealObservationStageRunner.RegisteredSparseUncertainty(0, 1, 2, 4));
    }
    private static PlateSolveEvidence Inferred() => new(new PlateSolveResult { Success = true },
        new Coordinates(20, 30, Epoch.J2000, Coordinates.RAType.Degrees), 0, "PL3",
        "current.fit", "inference.json", new string('A', 64), 1920, 1080, 1, DateTimeOffset.UtcNow)
    {
        Authority = "RegisteredMosaicWcs", InferenceValidUntilUtc = DateTimeOffset.UtcNow.AddMinutes(1),
        InferenceSources = new Dictionary<string, string>
        {
            ["current.fit"] = new('B', 64), ["inference.json"] = new('A', 64),
            ["parent.fit"] = new('C', 64), ["mosaic.fit"] = new('D', 64),
            ["native-mosaic-solve.json"] = new('E', 64), ["focus.json"] = new('F', 64)
        }
    };

    [Fact]
    public async Task EveryParentIncludingDerivedPixelsAndFocusIsCheckedBeforeMotion()
    {
        var solve = Inferred();
        var checkedPaths = new List<string>();
        var gate = await RealObservationStageRunner.ValidateSparseInferenceSourcesAsync(solve,
            (path, _) => { checkedPaths.Add(path); return Task.FromResult(solve.InferenceSources[path]); }, default);
        Assert.Equal(GateDisposition.Passed, gate.Disposition);
        Assert.Equal(solve.InferenceSources.Keys, checkedPaths);
        Assert.False(solve.IsFormal);
    }

    [Theory]
    [InlineData("parent.fit")]
    [InlineData("mosaic.fit")]
    [InlineData("native-mosaic-solve.json")]
    [InlineData("focus.json")]
    [InlineData("current.fit")]
    [InlineData("inference.json")]
    public async Task MutationCannotBePromotedToCurrentPosition(string changed)
    {
        var solve = Inferred();
        var gate = await RealObservationStageRunner.ValidateSparseInferenceSourcesAsync(solve,
            (path, _) => Task.FromResult(path == changed ? new string('0', 64) : solve.InferenceSources[path]), default);
        Assert.Equal("G3_SPARSE_SOURCE_CHANGED", gate.Code);
        Assert.NotEqual(GateDisposition.Passed, gate.Disposition);
    }

    [Fact]
    public async Task ExpiredOpticalPositionIsQualityMissNotFabricatedFreshness()
    {
        var gate = await RealObservationStageRunner.ValidateSparseInferenceSourcesAsync(
            Inferred() with { InferenceValidUntilUtc = DateTimeOffset.UtcNow.AddSeconds(-1) },
            (_, _) => throw new Xunit.Sdk.XunitException("Expired evidence must not be consumed"), default);
        Assert.Equal("G3_CATALOG_SHORT_POSITION_UNCONFIRMED", gate.Code);
        Assert.NotEqual(GateDisposition.Passed, gate.Disposition);
    }

    [Fact]
    public async Task CancellationDoesNotBecomeOptionalQualityFallback()
    {
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            RealObservationStageRunner.ValidateSparseInferenceSourcesAsync(Inferred(),
                (_, _) => Task.FromResult(new string('A', 64)), stop.Token));
    }

    [Fact]
    public async Task ParentRehashLatencyCannotOutliveTheOpticalWitness()
    {
        var solve = Inferred() with { InferenceValidUntilUtc = DateTimeOffset.UtcNow.AddMilliseconds(100) };
        var gate = await RealObservationStageRunner.ValidateSparseInferenceSourcesAsync(solve,
            async (path, _) =>
            {
                if (path == "focus.json") await Task.Delay(150);
                return solve.InferenceSources[path];
            }, default);
        Assert.Equal("G3_CATALOG_SHORT_POSITION_UNCONFIRMED", gate.Code);
    }

    [Theory]
    [InlineData("G3_SPARSE_SOURCE_CHANGED")]
    [InlineData("QHY_G3_LOCAL_REFRESH_STOP_UNCONFIRMED")]
    public async Task SourceMutationAndUnconfirmedPhotometryStopActuallyPreventTheNextNativeTarget(string code)
    {
        var solve = Inferred();
        GateResult produced;
        if (code == "G3_SPARSE_SOURCE_CHANGED")
        {
            // Use the actual production source validator, not an inferred
            // quality label for a corrupted parent/focus observation.
            produced = await RealObservationStageRunner.ValidateSparseInferenceSourcesAsync(solve,
                (path, _) => Task.FromResult(path == "focus.json" ? new string('0', 64) : solve.InferenceSources[path]), default);
        }
        else
        {
            // Finite owner port mirrors CaptureLocalDualWitnessAsync's typed
            // nonterminal stop result. No cancellation receipt is fabricated.
            produced = GateResult.Unknown(code, "Owned QHY witness did not reach a checked terminal boundary.");
        }
        var entered = new List<string>();
        var expected = new PhysicalActionGateException(produced);
        var error = await Assert.ThrowsAsync<PhysicalActionGateException>(() => G3SparseRecoveryWorkflow.ResolveAsync(
            new StageResult(GateResult.TargetQuality("G3_PLATE_SOLVE_LADDER_EXHAUSTED", "Optical miss before recovery.")),
            false, true,
            _ => code == "G3_SPARSE_SOURCE_CHANGED" ? Task.FromException(expected) : Task.CompletedTask,
            _ => Task.CompletedTask,
            _ => { entered.Add("registered"); return Task.FromResult<StageResult?>(null); },
            _ => { entered.Add("local-dual"); return Task.FromException<StageResult?>(expected); },
            _ => { entered.Add("mosaic"); return Task.FromResult<StageResult?>(null); }, default));
        Assert.Same(expected, error);
        Assert.Equal(code, error.Gate.Code);
        Assert.Equal(GateFailureScope.Run, error.Gate.FailureScope);
        Assert.False(NativeSequencePlan.IsSkippableQualityFailure(error.Gate));
        if (code == "G3_SPARSE_SOURCE_CHANGED") Assert.Empty(entered);
        else Assert.Equal(new[] { "registered", "mosaic", "local-dual" }, entered);
        // TWO real native target/coordinator stacks: the second must not reach
        // FinalizeObservation even though its saved policy is SkipQualityFailure.
        await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync("unknown-gate", code, error.Gate,
            ObservationStage.AcquireG3SlitField);
    }
}
