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

    [Theory]
    [InlineData("valid")]
    [InlineData("old-association")]
    [InlineData("old-measurement")]
    [InlineData("mutated-short")]
    [InlineData("mutated-parent")]
    [InlineData("different-target")]
    [InlineData("extended-expiry")]
    [InlineData("drift-too-large")]
    [InlineData("cancel")]
    public async Task FreshIndependentPairTakesPositionAuthorityNotAnUnboundedWcsExtension(string scenario)
    {
        var now = DateTimeOffset.UtcNow;
        var solve = Inferred() with { InferenceValidUntilUtc = now.AddSeconds(-5) };
        var target = new StarCandidate(new(173, 810), 4352, 6495, 25.8, 0, .389, 0, 173);
        var identification = new TargetIdentification(GateResult.Pass("measured", "independent pair"),
            target, new(144, 825), 32.8, 2, TargetIdentificationAuthority.CatalogWcsProjection,
            BoundShortPositionEvidencePath: "confirmed.json", CatalogPositionSpreadPixels: 1.89);
        var proof = new G3SparseMeasuredContinuity(now.AddSeconds(-6), now.AddSeconds(20), solve.EvidenceSha256,
            "confirmed.json", new('3', 64), "first.fit", new('1', 64), "second.fit", new('2', 64), 100);
        var track = new G3MeasuredTargetSource("second.fit", new('2', 64), null!, null!, null!,
            new(now.AddSeconds(-10), now.AddSeconds(-7), .01, now.AddSeconds(-6)), target.Centroid);
        var field = G3FieldState.Failed(GateResult.Pass("confirmed", "pair"), solve: solve) with
        { TargetIdentification = identification, TargetTrackSource = track, SparseInferenceConfirmed = true,
            SparseMeasuredContinuity = proof };
        if (scenario == "old-association") field = field with { SparseMeasuredContinuity = proof with { AssociationValidatedUtc = now } };
        if (scenario == "old-measurement") field = field with { SparseMeasuredContinuity = proof with { PositionValidUntilUtc = now.AddSeconds(-1) } };
        if (scenario == "different-target") field = field with { TargetTrackSource = track with { Target = new(174, 810) } };
        if (scenario == "extended-expiry") field = field with { SparseMeasuredContinuity = proof with { PositionValidUntilUtc = now.AddMinutes(1) } };
        if (scenario == "drift-too-large") field = field with { Solve = solve with { MeasuredDrift = new(100, "registered.json") } };
        var hashes = new Dictionary<string, string>(solve.InferenceSources)
        { [proof.ConfirmationPath] = proof.ConfirmationSha256, [proof.FirstFramePath] = proof.FirstFrameSha256,
            [proof.CurrentFramePath] = proof.CurrentFrameSha256 };
        if (scenario == "mutated-short") hashes[proof.FirstFramePath] = new('9', 64);
        if (scenario == "mutated-parent") hashes["parent.fit"] = new('9', 64);
        using var stop = new CancellationTokenSource();
        if (scenario == "cancel") stop.Cancel();
        Task<GateResult> Validate() => RealObservationStageRunner.ValidateMeasuredSparseContinuityAsync(field,
            (p, _) => Task.FromResult(hashes[p]), stop.Token);
        if (scenario == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(Validate);
        else Assert.Equal(scenario == "valid", (await Validate()).Disposition == GateDisposition.Passed);
        Assert.Equal(now.AddSeconds(-5), solve.InferenceValidUntilUtc);
        Assert.False(solve.IsFormal);
    }

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
    [InlineData("typed-skip")]
    [InlineData("typed-cleanup-failure")]
    [InlineData("late-unsafe")]
    public async Task ExpiredMid0PositionActuallyExecutesNextNativeTargetOnlyAfterCheckedStop(string boundary)
    {
        var gate = await RealObservationStageRunner.ValidateSparseInferenceSourcesAsync(
            Inferred() with { InferenceValidUntilUtc = DateTimeOffset.UtcNow.AddSeconds(-1) },
            (_, _) => throw new Xunit.Sdk.XunitException("Expired evidence cannot be consumed"), default);
        await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync(boundary, gate.Code, gate,
            ObservationStage.AcquireG3SlitField);
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
