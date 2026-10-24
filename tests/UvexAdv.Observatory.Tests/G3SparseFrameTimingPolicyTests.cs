using Xunit;

namespace UvexAdv.Observatory.Tests;

public sealed class G3SparseFrameTimingPolicyTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-24T01:00:00Z");
    private static G3SparseExposureTiming Current => new(Now.AddSeconds(-25), Now.AddSeconds(-13), 10, Now.AddSeconds(-1));

    [Fact]
    public void NativePcBracketRetainsMidpointIntervalAndSolveLatency()
    {
        var result = Evaluate(Current, measured: new(.1, "two-current-pointing-images.json"));
        Assert.True(result.Usable);
        Assert.Equal(Now.AddSeconds(-20), Current.EarliestMidpointUtc);
        Assert.Equal(Now.AddSeconds(-18), Current.LatestMidpointUtc);
        Assert.Equal(Now.AddSeconds(-19), Current.MidpointUtc);
        Assert.Equal(20, result.CurrentMidpointAgeUpperSeconds);
        Assert.Equal(5.5, result.UncertaintyPixels, 9); // 3 + .1 * (20 + 5)
        Assert.Equal(1, result.ExposureDriftPixels);
        Assert.True(result.RequiresFreshShortTargetPair);
        Assert.False(result.MotionAuthority);
        Assert.Equal(12, result.Gate.Metrics!["solveAfterCaptureSeconds"]);
    }

    [Fact]
    public void RegisteredPixelsWithUnknownDriftCannotMasqueradeAsTemporalPositionPrediction()
    {
        var registered = Evaluate(Current);
        var extrapolated = Evaluate(Current, purpose: G3SparseTimingPurpose.ExtrapolatedPosition);
        Assert.True(registered.Usable);
        Assert.False(registered.DriftWasMeasured);
        Assert.Null(registered.ExposureDriftPixels);
        Assert.False(registered.TemporalExtrapolationAuthorized);
        Assert.False(registered.Gate.Metrics!.ContainsKey("measuredDriftUpperPixelsPerSecond"));
        Assert.False(extrapolated.Usable);
        Assert.Equal("G3_SPARSE_TIMING_DRIFT_UNMEASURED", extrapolated.Gate.Code);
        Assert.Equal(GateDisposition.Passed, extrapolated.Gate.Disposition);
        Assert.Equal(GateSeverity.Warning, extrapolated.Gate.Severity);
    }

    [Fact]
    public void MeasuredDriftAdmitsOnlyAnUncertaintyBoundNotAMotionCorrection()
    {
        var result = Evaluate(Current, purpose: G3SparseTimingPurpose.ExtrapolatedPosition,
            measured: new(.2, "verified-QHY-WCS-pair.json"));
        Assert.True(result.Usable);
        Assert.True(result.TemporalExtrapolationAuthorized);
        Assert.Equal(8, result.UncertaintyPixels, 9);
        Assert.False(result.MotionAuthority);
        Assert.True(result.RequiresFreshShortTargetPair);
        var highDrift = Evaluate(Current, measured: new(5, "same-pointing-registration.json"));
        Assert.False(highDrift.Usable);
        Assert.Equal("G3_SPARSE_TIMING_UNCERTAINTY_EXCEEDED", highDrift.Gate.Code);
    }

    [Theory]
    [InlineData("old")]
    [InlineData("reversed")]
    [InlineData("future")]
    [InlineData("duration-longer-than-bracket")]
    [InlineData("solve-before-capture")]
    [InlineData("missing")]
    public void InvalidOrOldTimingRejectsOnlyThisFallback(string failure)
    {
        var timing = failure switch
        {
            "old" => new G3SparseExposureTiming(Now.AddSeconds(-70), Now.AddSeconds(-58), 10, Now.AddSeconds(-1)),
            "reversed" => Current with { CaptureRequestedUtc = Now },
            "future" => Current with { SolveCompletedUtc = Now.AddSeconds(1) },
            "duration-longer-than-bracket" => Current with { ExposureSeconds = 13 },
            "solve-before-capture" => Current with { SolveCompletedUtc = Now.AddSeconds(-15) },
            _ => Current with { CaptureRequestedUtc = default },
        };
        var result = Evaluate(timing);
        Assert.False(result.Usable);
        Assert.Equal(GateDisposition.Passed, result.Gate.Disposition);
        Assert.Equal(GateSeverity.Warning, result.Gate.Severity);
        Assert.Equal(GateFailureScope.Run, result.Gate.FailureScope); // Not a new terminal target/night gate.
        Assert.False(result.MotionAuthority);
    }

    [Fact]
    public void ExposureAndSlowSolvingAgeThePixelsEvenWhenSolveJustCompleted()
    {
        var result = Evaluate(new(Now.AddSeconds(-60), Now.AddSeconds(-43), 15, Now));
        Assert.False(result.Usable);
        Assert.Equal("G3_SPARSE_TIMING_CURRENT_STALE", result.Gate.Code);
        Assert.Equal(52.5, result.CurrentMidpointAgeUpperSeconds);
    }

    [Fact]
    public void SolveCompletionCannotRefreshAnOldImageAcrossARegistrationGap()
    {
        var previous = new G3SparseExposureTiming(Now.AddSeconds(-90), Now.AddSeconds(-78), 10, Now.AddSeconds(-2));
        var result = Evaluate(Current, connected: [previous]);
        Assert.False(result.Usable);
        Assert.Equal("G3_SPARSE_TIMING_GAP_EXCEEDED", result.Gate.Code);
        Assert.Equal(67, result.MaximumAdjacentGapUpperSeconds);
    }

    [Fact]
    public void ConnectedSpanCannotGrowUnboundedThroughOtherwiseCloseNeighbours()
    {
        var previous = new[] { -150, -120, -90, -60 }.Select(s =>
            new G3SparseExposureTiming(Now.AddSeconds(s), Now.AddSeconds(s + 12), 10, Now.AddSeconds(s + 20))).ToArray();
        var result = Evaluate(Current, connected: previous);
        Assert.False(result.Usable);
        Assert.Equal("G3_SPARSE_TIMING_SPAN_EXCEEDED", result.Gate.Code);
        Assert.Equal(127, result.ConnectedSpanUpperSeconds);
    }

    [Fact]
    public void DriftProvenanceIsRequiredAndUnknownCannotBecomeDefaultZero()
    {
        foreach (var invalid in new[] { new G3SparseDriftBound(double.NaN, "measured"), new(-1, "measured"), new(0, "") })
        {
            var result = Evaluate(Current, measured: invalid);
            Assert.False(result.Usable);
            Assert.Equal("G3_SPARSE_TIMING_DRIFT_INVALID", result.Gate.Code);
        }
    }

    private static G3SparseTimingAssessment Evaluate(G3SparseExposureTiming current,
        G3SparseTimingPurpose purpose = G3SparseTimingPurpose.RegisteredCurrentFrame,
        G3SparseDriftBound? measured = null, IReadOnlyList<G3SparseExposureTiming>? connected = null) =>
        G3SparseFrameTimingPolicy.Evaluate(current, Now, purpose, 3, 100, connected, measured);
}
