using System.Text.Json;
using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Observatory.Tests;

public sealed class SevereDefocusPolicyTests
{
    private static SepFocusFrame Frame(int count = 4, double r50 = 11, double r80 = 17)
    {
        var points = new[] { (80d, 80d), (300d, 80d), (80d, 300d), (300d, 300d), (500d, 180d) }.Take(count).ToArray();
        return new(points.Select(p => new SepFocusReference(p.Item1, p.Item2)).ToArray(),
            points.Select((p, id) => new SepFocusStar(id, p.Item1, p.Item2, r50, r80, 10000 + id * 1000, 80, .9, 25)).ToArray());
    }

    private static SepFocusFrame Change(SepFocusFrame frame, Func<SepFocusStar, SepFocusStar> change) =>
        frame with { Stars = frame.Stars.Select(change).ToArray() };

    private static void NoMotionCandidate(SevereDefocusDecision result, string code)
    {
        Assert.False(result.ShouldAttemptFocus);
        Assert.Equal(code, result.Gate.Code);
        // A diagnostic decision must never become a new observation hard stop.
        Assert.Equal(GateDisposition.Passed, result.Gate.Disposition);
        Assert.Empty(result.StarIds);
    }

    [Fact]
    public void RepeatedIndependentBroadStarsProduceOnlyCandidateNotFocusVerification()
    {
        var first = Frame();
        var second = Change(first, s => s with { X = s.X + 1.5, Y = s.Y - .5, Flux = s.Flux * .95, R50 = s.R50 * 1.02 });
        var result = SevereDefocusPolicy.Evaluate(first, second);
        Assert.True(result.ShouldAttemptFocus);
        Assert.Equal("G3_SEVERE_DEFOCUS_CANDIDATE", result.Gate.Code);
        Assert.Equal(GateSeverity.Warning, result.Gate.Severity);
        Assert.Equal(new[] { 0, 1, 2, 3 }, result.StarIds);
        Assert.Contains("不是", result.Gate.Message);
        Assert.All(result.Gate.Metrics!.Values, value => Assert.True(double.IsFinite(value)));
        Assert.Equal("SEP-severe-defocus-candidate-v1", SevereDefocusPolicy.Version);
        Assert.Equal(10, result.Gate.Metrics["severeR50Pixels"]);
        Assert.Equal(15, result.Gate.Metrics["severeR80Pixels"]);
        Assert.Equal(11, first.Stars[0].R50); // Inputs remain immutable.
    }

    [Theory]
    [InlineData(4.6069, 8.8419)] // 2026-09-14 verified-origin A1.
    [InlineData(4.5522, 8.5723)] // 2026-09-14 fresh final verified origin.
    [InlineData(7.5, 12.5)] // Moderate broadening is not severe defocus.
    [InlineData(10.2, 14.9)] // Both energy radii must be large.
    [InlineData(9.9, 16.0)]
    public void HistoricNormalFocusAndOrdinaryBroadeningDoNotTrigger(double r50, double r80)
    {
        var frame = Frame(r50: r50, r80: r80);
        NoMotionCandidate(SevereDefocusPolicy.Evaluate(frame, frame), "G3_DEFOCUS_NOT_SEVERE");
    }

    [Fact]
    public void OneBroadTargetOrGhostDoesNotRepresentTheField()
    {
        var frame = Change(Frame(r50: 5, r80: 9), s => s.Id == 0 ? s with { R50 = 13, R80 = 19 } : s);
        NoMotionCandidate(SevereDefocusPolicy.Evaluate(frame, frame), "G3_DEFOCUS_NOT_SEVERE");
        var onlyTwo = Frame(2);
        NoMotionCandidate(SevereDefocusPolicy.Evaluate(onlyTwo, onlyTwo), "G3_SEVERE_DEFOCUS_INVALID_SAMPLES");
    }

    [Fact]
    public void ThreeBroadStarsMustAlsoBeMostOfTheCommonField()
    {
        var frame = Change(Frame(5), s => s.Id >= 3 ? s with { R50 = 5, R80 = 9 } : s);
        NoMotionCandidate(SevereDefocusPolicy.Evaluate(frame, frame), "G3_DEFOCUS_NOT_SEVERE");
        var four = Change(Frame(4), s => s.Id == 3 ? s with { R50 = 5, R80 = 9 } : s);
        Assert.True(SevereDefocusPolicy.Evaluate(four, four).ShouldAttemptFocus);
    }

    [Fact]
    public void MissingRepeatedStarsOrChangingReferenceCannotInventAConsistentEnsemble()
    {
        var first = Frame(5);
        var lost = first with { Stars = first.Stars.Take(3).ToArray() };
        NoMotionCandidate(SevereDefocusPolicy.Evaluate(first, lost), "G3_SEVERE_DEFOCUS_STARS_INSUFFICIENT");
        var otherReference = first with { Reference = first.Reference.Select(r => r with { X = r.X + .01 }).ToArray() };
        NoMotionCandidate(SevereDefocusPolicy.Evaluate(first, otherReference), "G3_SEVERE_DEFOCUS_REFERENCE_CHANGED");
    }

    [Theory]
    [InlineData(.79, 1.0)]
    [InlineData(1.26, 1.0)]
    [InlineData(1.0, .79)]
    [InlineData(1.0, 1.21)]
    public void ChangingTransparencyOrRadiusDoesNotBecomeFocusDiagnosis(double fluxFactor, double radiusFactor)
    {
        var first = Frame();
        var second = Change(first, s => s with { Flux = s.Flux * fluxFactor, R50 = s.R50 * radiusFactor });
        NoMotionCandidate(SevereDefocusPolicy.Evaluate(first, second), "G3_SEVERE_DEFOCUS_TRANSPARENCY_OR_SHAPE_CHANGED");
    }

    [Fact]
    public void ASingleVariableStarCannotHideBehindTheGroupMedian()
    {
        var first = Frame();
        var second = Change(first, s => s.Id == 0 ? s with { Flux = s.Flux / 2 } : s);
        NoMotionCandidate(SevereDefocusPolicy.Evaluate(first, second), "G3_SEVERE_DEFOCUS_TRANSPARENCY_OR_SHAPE_CHANGED");
    }

    [Fact]
    public void ObviousTrailsCannotTriggerAutomaticFocus()
    {
        var frame = Change(Frame(), s => s with { AxisRatio = .49 });
        NoMotionCandidate(SevereDefocusPolicy.Evaluate(frame, frame), "G3_SEVERE_DEFOCUS_STARS_INSUFFICIENT");
    }

    [Fact]
    public void FixedQualityCohortReusesFocusSnrFloorAndDoesNotRequireRoundStars()
    {
        var frame = Change(Frame(), s => s with { AxisRatio = .55, Snr = 15.1 });
        Assert.True(SevereDefocusPolicy.Evaluate(frame, frame).ShouldAttemptFocus);
        var marginal = Change(frame, s => s.Id == 3 ? s with { AxisRatio = .3 } : s);
        var result = SevereDefocusPolicy.Evaluate(marginal, marginal);
        Assert.True(result.ShouldAttemptFocus);
        Assert.Equal(new[] { 0, 1, 2 }, result.StarIds);
        Assert.Equal(3, result.Gate.Metrics!["selectedCohortStars"]);
        Assert.Equal(1, result.Gate.Metrics["excludedCommonStars"]);
    }

    [Fact]
    public void QualityCohortIsChosenBeforeBlurAndCannotCherryPickBroadStars()
    {
        var frame = Change(Frame(5), s => s.Id == 4 ? s with { AxisRatio = .3 } :
            s.Id >= 2 ? s with { R50 = 5, R80 = 9 } : s);
        NoMotionCandidate(SevereDefocusPolicy.Evaluate(frame, frame), "G3_DEFOCUS_NOT_SEVERE");
    }

    [Fact]
    public void LocalDonutKnotsOrCompactGhostClusterAreNotIndependentFieldStars()
    {
        var close = Change(Frame(), s => s with { X = 80 + s.Id * 12, Y = 80 });
        NoMotionCandidate(SevereDefocusPolicy.Evaluate(close, close), "G3_SEVERE_DEFOCUS_STARS_NOT_INDEPENDENT");
        var clustered = Frame(3) with { Stars = [
            new(0, 80, 80, 11, 17, 10000, 80, .9, 25),
            new(1, 140, 80, 11, 17, 10000, 80, .9, 25),
            new(2, 110, 132, 11, 17, 10000, 80, .9, 25)] };
        NoMotionCandidate(SevereDefocusPolicy.Evaluate(clustered, clustered), "G3_SEVERE_DEFOCUS_FIELD_COVERAGE_LOW");
    }

    [Fact]
    public void TrackingDriftAndMismatchedStarDisplacementDoNotTrigger()
    {
        var first = Frame();
        var drift = Change(first, s => s with { X = s.X + 6 });
        NoMotionCandidate(SevereDefocusPolicy.Evaluate(first, drift), "G3_SEVERE_DEFOCUS_FIELD_UNSTABLE");
        var mismatch = Change(first, s => s.Id == 0 ? s with { X = s.X + 3 } : s);
        NoMotionCandidate(SevereDefocusPolicy.Evaluate(first, mismatch), "G3_SEVERE_DEFOCUS_FIELD_UNSTABLE");
    }

    [Fact]
    public void LegacyProtocolRemainsValidForManualFocusButCannotTriggerAutomaticRecovery()
    {
        var legacy = Change(Frame(), s => s with { AxisRatio = null, ApertureRadius = null });
        legacy.Validate(1920, 1080);
        NoMotionCandidate(SevereDefocusPolicy.Evaluate(legacy, legacy), "G3_SEVERE_DEFOCUS_SHAPE_UNAVAILABLE");
    }

    [Fact]
    public void ExistingSnakeCaseWorkerPayloadPreservesShapeAndApertureWithoutProtocolChanges()
    {
        const string payload = """
            {"reference":[{"x":80,"y":80}],"stars":[{"id":0,"x":80,"y":80,"r50":11,"r80":17,"flux":10000,"snr":80,"axis_ratio":0.9,"aperture_radius":25}]}
            """;
        var parsed = JsonSerializer.Deserialize<SepFocusFrame>(payload, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower })!;
        parsed.Validate(1920, 1080);
        Assert.Equal(.9, parsed.Stars[0].AxisRatio);
        Assert.Equal(25, parsed.Stars[0].ApertureRadius);
        var old = JsonSerializer.Deserialize<SepFocusStar>("""{"Id":0,"X":80,"Y":80,"R50":5,"R80":9,"Flux":10000,"Snr":80}""")!;
        Assert.Null(old.AxisRatio);
        Assert.Null(old.ApertureRadius);
    }

    [Fact]
    public void InvalidOrClippedAperturesNeverBecomeAutomaticFocusAuthority()
    {
        var first = Frame();
        foreach (var invalid in new[] {
            first with { Error = "No three-star translation consensus" },
            Change(first, s => s with { R50 = double.NaN }),
            Change(first, s => s with { AxisRatio = double.PositiveInfinity }),
            Change(first, s => s with { R80 = 22 }),
            first with { Stars = [first.Stars[0], first.Stars[0], first.Stars[1]] } })
            NoMotionCandidate(SevereDefocusPolicy.Evaluate(first, invalid), "G3_SEVERE_DEFOCUS_INVALID_SAMPLES");
        Assert.Throws<InvalidDataException>(() => Change(first, s => s with { AxisRatio = 1.1 }).Validate(1920, 1080));
        Assert.Throws<InvalidDataException>(() => Change(first, s => s with { ApertureRadius = 19 }).Validate(1920, 1080));
    }

    [Fact]
    public void ExtremeFiniteFluxCannotPublishNonFiniteDiagnostics()
    {
        var first = Frame();
        var second = Change(first, s => s with { Flux = double.Epsilon });
        var result = SevereDefocusPolicy.Evaluate(first, second);
        NoMotionCandidate(result, "G3_SEVERE_DEFOCUS_TRANSPARENCY_OR_SHAPE_CHANGED");
        Assert.All(result.Gate.Metrics!.Values, value => Assert.True(double.IsFinite(value)));
    }
}
