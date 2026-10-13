using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class TargetFailureContinuationTests
{
    public static IEnumerable<object[]> NativeCases()
    {
        foreach (var failure in new[] { "return-reacquire", "relock-reacquire", "atr-clipping", "off-slit", "empty-g3" })
        foreach (var boundary in new[] { "skip", "cleanup-failure", "end-policy" })
            yield return new object[] { failure, boundary };
    }

    [Theory]
    [MemberData(nameof(NativeCases))]
    public async Task ProductionFailureActuallyRunsNextNativeTargetOnlyAfterCheckedBoundary(string failure, string boundary)
    {
        GateResult gate;
        var stage = ObservationStage.PlaceTargetOnSlit;
        switch (failure)
        {
            case "return-reacquire":
            case "relock-reacquire":
                // Exercise both typed producer results and historical known-quality results.
                var original = failure == "return-reacquire"
                    ? GateResult.TargetQuality("G3_POST_WCS_SHORT_UNCONFIRMED", "Fresh optical samples exhausted.")
                    : GateResult.Unknown("G3_BOUNDED_SEARCH_EXHAUSTED_RETURNED", "Legacy returned acquisition failure.");
                gate = ObservationWorkflowResults.WithFailureContext(new(original), failure).Gate;
                break;
            case "atr-clipping":
                var fixture = new AtrWorkflowFixture(1, [30]);
                fixture.Port.NextMetrics = m => m with { HighPercentileAdu = 65000 };
                gate = (await fixture.Science.SelectExposureAsync(fixture.Context, default)).Gate;
                Assert.Equal("NO_SAFE_SATURATION_BACKOFF", gate.Code);
                Assert.Single(fixture.Port.Saved);
                Assert.Equal(0, fixture.Science.AcceptedFrames);
                stage = ObservationStage.SelectAtrExposure;
                break;
            case "off-slit":
                gate = new Phd2NativeGuideSelectionExhaustedException(4, ["Native candidates vetoed."])
                    .AfterCheckedStop().Gate;
                break;
            default:
                gate = RealObservationStageRunner.ClassifyG3EmptyLadder(EmptyAttempts(), 2);
                var retries = new ObservationAutomaticRecoverySession();
                for (var i = 0; i < 3; i++)
                    Assert.True(retries.Evaluate(ObservationStage.AcquireG3SlitField, gate).ShouldRetry);
                Assert.True(retries.Evaluate(ObservationStage.AcquireG3SlitField, gate).Exhausted);
                stage = ObservationStage.AcquireG3SlitField;
                break;
        }
        Assert.True(NativeSequencePlan.IsSkippableQualityFailure(gate));
        // Executes TWO N.I.N.A. containers and their canonical coordinator, including
        // manifest-join race and cleanup. Asserts second FinalizeObservation actually ran.
        await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync(boundary, gate.Code, gate, stage);
    }

    [Theory]
    [InlineData("PHD2_STOP_UNCONFIRMED")]
    [InlineData("PHD2_IDENTITY_STALE")]
    [InlineData("NIGHT_UNSAFE")]
    [InlineData("UNKNOWN_NEW_FAILURE")]
    public async Task ReacquisitionContextCannotConvertSafetyOrUnknownFailureIntoTargetQuality(string code)
    {
        var original = new StageResult(GateResult.Unknown(code, "Original failure", new Dictionary<string, double> { ["measurement"] = 7 }),
            "original.json", new Dictionary<string, string> { ["owner"] = "original" });
        var result = ObservationWorkflowResults.WithFailureContext(original, "Reacquisition failed.");
        Assert.Equal(code, result.Gate.Code);
        Assert.Equal(original.Gate.FailureScope, result.Gate.FailureScope);
        Assert.Same(original.Gate.Metrics, result.Gate.Metrics);
        Assert.Same(original.Metadata, result.Metadata);
        Assert.Equal(original.EvidencePath, result.EvidencePath);
        await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync("unknown-gate", code, result.Gate);
    }

    [Theory]
    [InlineData("invalid-buffer")]
    [InlineData("capture-transient")]
    [InlineData("solver-transient")]
    [InlineData("missing-content")]
    [InlineData("missing-binding")]
    [InlineData("missing-tier")]
    public async Task EmptyImageCannotHideInvalidCaptureEvidenceAndRunNextTarget(string invalid)
    {
        var attempts = EmptyAttempts();
        attempts[1] = invalid switch
        {
            "invalid-buffer" => attempts[1] with { GateCode = "G3_SOLVE_PROBE_PIXEL_BUFFER_UNSUPPORTED" },
            "capture-transient" => attempts[1] with { GateCode = "G3_PLATE_SOLVE_CAPTURE_TRANSIENT_TIER_SKIPPED" },
            "solver-transient" => attempts[1] with { GateCode = "G3_PLATE_SOLVE_SOLVER_TRANSIENT_TIER_SKIPPED" },
            "missing-content" => attempts[1] with { ContentGateCode = null },
            "missing-binding" => attempts[1] with { MountBinding = null },
            _ => attempts[1],
        };
        var gate = RealObservationStageRunner.ClassifyG3EmptyLadder(invalid == "missing-tier" ? attempts[..1] : attempts, 2);
        Assert.Equal(GateFailureScope.Run, gate.FailureScope);
        await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync("unknown-gate", gate.Code, gate);
    }

    [Fact]
    public async Task AtrInvalidMetricsAreNotClippingQualityFailure()
    {
        var f = new AtrWorkflowFixture();
        f.Port.NextMetrics = m => m with { HighPercentileAdu = double.NaN };
        var gate = (await f.Science.SelectExposureAsync(f.Context, default)).Gate;
        Assert.Equal(GateFailureScope.Run, gate.FailureScope);
        Assert.False(NativeSequencePlan.IsSkippableQualityFailure(gate));
        await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync("unknown-gate", gate.Code, gate, ObservationStage.SelectAtrExposure);
    }

    private static G3PlateSolveAttemptEvidence[] EmptyAttempts() => Enumerable.Range(1, 2).Select(i =>
    {
        var path = $"synthetic-{i}.fits";
        var binding = new G3FieldMountBinding(1, "test-run", "test-action", "test-preset", path,
            "test-sha", DateTimeOffset.UtcNow, 45, 40, "J2000", "East", DateTimeOffset.UtcNow, "test-binding");
        return new G3PlateSolveAttemptEvidence(i, i * 1000, "G3_PLATE_SOLVE_TIER_FAILED",
            GateDisposition.Indeterminate, false, path, $"attempt-{i}.json", "G3_CLOUD_OR_TRANSPARENCY_INVALID", 0, binding);
    }).ToArray();
}
