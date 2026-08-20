using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Observatory.Tests;

public sealed class BrightTargetCentroidAnalysisTests
{
    [Fact]
    public void UniqueSaturatedSourceUsesUnsaturatedWingsAndIsNeverFocusEvidence()
    {
        var frame = SyntheticFrame(180, 140, [(91.3, 68.7, 120_000d, 6.5)]);

        var result = BrightTargetWingCentroidAnalyzer.Analyze(frame);

        Assert.Equal(GateDisposition.Passed, result.Gate.Disposition);
        Assert.Equal("BRIGHT_TARGET_WING_CENTROID_VALID", result.Gate.Code);
        Assert.NotNull(result.Target);
        Assert.InRange(Math.Abs(result.Target!.Centroid.X - 91.3), 0, 0.35);
        Assert.InRange(Math.Abs(result.Target.Centroid.Y - 68.7), 0, 0.35);
        Assert.True(result.Target.SaturatedCorePixels >= 3);
        Assert.True(result.Target.WingPixels >= 48);
        Assert.False(result.FocusEligible);
    }

    [Fact]
    public void UnsaturatedFrameCannotEnterExceptionalBranch()
    {
        var frame = SyntheticFrame(180, 140, [(90d, 70d, 30_000d, 5d)]);

        var result = BrightTargetWingCentroidAnalyzer.Analyze(frame);

        Assert.Equal(GateDisposition.Failed, result.Gate.Disposition);
        Assert.Equal("BRIGHT_TARGET_SATURATED_CORE_NOT_FOUND", result.Gate.Code);
        Assert.False(result.FocusEligible);
    }

    [Fact]
    public void SimilarSeparatedSaturatedSourcesAreAmbiguous()
    {
        var frame = SyntheticFrame(
            260,
            150,
            [(70d, 75d, 120_000d, 6d), (190d, 75d, 116_000d, 6d)]);

        var result = BrightTargetWingCentroidAnalyzer.Analyze(frame);

        Assert.Equal(GateDisposition.Indeterminate, result.Gate.Disposition);
        Assert.Equal("BRIGHT_TARGET_AMBIGUOUS", result.Gate.Code);
        Assert.False(result.FocusEligible);
    }

    [Fact]
    public void EdgeTruncatedSaturatedSourceIsRejected()
    {
        var frame = SyntheticFrame(180, 140, [(20d, 70d, 120_000d, 6d)]);

        var result = BrightTargetWingCentroidAnalyzer.Analyze(frame);

        Assert.Equal(GateDisposition.Indeterminate, result.Gate.Disposition);
        Assert.Equal("BRIGHT_TARGET_WINGS_UNUSABLE", result.Gate.Code);
        Assert.Contains(result.Candidates, candidate => candidate.Gate.Code == "BRIGHT_TARGET_EDGE_TRUNCATED");
    }

    [Fact]
    public void NearbySaturatedNeighborIsRejectedAsBlend()
    {
        var frame = SyntheticFrame(
            200,
            150,
            [(82d, 75d, 120_000d, 5d), (118d, 75d, 95_000d, 5d)]);

        var result = BrightTargetWingCentroidAnalyzer.Analyze(frame);

        Assert.Equal(GateDisposition.Indeterminate, result.Gate.Disposition);
        Assert.Contains(result.Candidates, candidate => candidate.Gate.Code == "BRIGHT_TARGET_SATURATED_NEIGHBOR");
    }

    [Fact]
    public void AuthorityRequiresFreshRunBoundWcsIndependentFocusAndExactMinimumExposure()
    {
        var now = new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);
        var evidence = ValidAuthority(now);
        var options = AuthorityOptions();

        var gate = BrightTargetAuthorityGate.Evaluate(evidence, options);

        Assert.Equal(GateDisposition.Passed, gate.Disposition);
        Assert.Equal("BRIGHT_TARGET_AUTHORITY_VALID", gate.Code);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("stale-qhy")]
    [InlineData("expired-focus")]
    [InlineData("focus-position")]
    [InlineData("wrong-exposure")]
    [InlineData("used-for-focus")]
    [InlineData("other-run")]
    [InlineData("missing-wcs-hash")]
    [InlineData("coordinate-mismatch")]
    public void AuthorityFailsClosedWhenAnyIndependentProofIsMissing(string mutation)
    {
        var now = new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);
        var evidence = ValidAuthority(now);
        evidence = mutation switch
        {
            "disabled" => evidence with { Enabled = false },
            "stale-qhy" => evidence with { QhyFrameCompletedUtc = now.AddMinutes(-11) },
            "expired-focus" => evidence with { C11FocusValidUntilUtc = now.AddSeconds(-1) },
            "focus-position" => evidence with { C11CurrentPositionSteps = 5001 },
            "wrong-exposure" => evidence with { G3ExposureMilliseconds = 101 },
            "used-for-focus" => evidence with { G3FrameUsedForFocus = true },
            "other-run" => evidence with { QhyObservationRunId = "other-run" },
            "missing-wcs-hash" => evidence with { QhyWcsEvidenceSha256 = string.Empty },
            "coordinate-mismatch" => evidence with { QhyRequestedRightAscensionDegrees = 12 },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };

        var gate = BrightTargetAuthorityGate.Evaluate(evidence, AuthorityOptions());

        Assert.Equal(GateDisposition.Indeterminate, gate.Disposition);
        Assert.Equal("BRIGHT_TARGET_AUTHORITY_WITHHELD", gate.Code);
    }

    [Fact]
    public void AuthorityDoesNotContainAProductTargetOrOpticalAxisOffset()
    {
        var properties = typeof(BrightTargetAuthorityEvidence).GetProperties().Select(property => property.Name).ToArray();

        Assert.DoesNotContain(properties, name => name.Contains("Enif", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(properties, name => name.Contains("Offset", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(properties, name => name.Contains("Boresight", StringComparison.OrdinalIgnoreCase));
    }

    private static BrightTargetAuthorityOptions AuthorityOptions() => new(
        MaximumQhyWcsAge: TimeSpan.FromMinutes(10),
        MaximumG3FrameAge: TimeSpan.FromMinutes(2),
        MaximumQhyTargetResidualArcseconds: 30,
        MaximumCatalogCoordinateMismatchArcseconds: 1,
        MinimumC11FocusConfidence: 0.7);

    private static BrightTargetAuthorityEvidence ValidAuthority(DateTimeOffset now) => new(
        Enabled: true,
        ObservationRunId: "run-1",
        CatalogTarget: new EquatorialTarget("Configured target", "HIP 123", 310.35798, 45.28034),
        QhyObservationRunId: "run-1",
        QhyRequestedTarget: "Configured target",
        QhyRequestedRightAscensionDegrees: 310.35798,
        QhyRequestedDeclinationDegrees: 45.28034,
        QhyCoordinateEpoch: "ICRS",
        QhyAcceptedFrameSha256: new string('A', 64),
        QhyFrameCompletedUtc: now.AddMinutes(-1),
        QhyWcsSucceeded: true,
        QhyWcsRequestedRightAscensionDegrees: 310.35798,
        QhyWcsRequestedDeclinationDegrees: 45.28034,
        QhyWcsResidualArcseconds: 8,
        QhyWcsEvidenceSha256: new string('B', 64),
        C11FocusEvidenceSha256: new string('C', 64),
        C11FocusMetricKind: FocusMetricKind.G3StellarShape,
        C11FocusSourceCameraStableId: "g3-stable-id",
        ExpectedG3SourceCameraStableId: "g3-stable-id",
        C11FocusMetricValue: 7.5,
        C11FocusVerifiedUtc: now.AddHours(-1),
        C11FocusValidUntilUtc: now.AddHours(2),
        C11FocusConfidence: 0.9,
        C11LockedPositionSteps: 5000,
        C11CurrentPositionSteps: 5000,
        G3FrameSha256: new string('D', 64),
        G3FrameCompletedUtc: now.AddSeconds(-5),
        G3ExposureMilliseconds: 100,
        ConfiguredMinimumG3ExposureMilliseconds: 100,
        G3FrameUsedForFocus: false,
        EvaluatedUtc: now);

    private static MonochromeFrame SyntheticFrame(
        int width,
        int height,
        IReadOnlyList<(double X, double Y, double Amplitude, double Sigma)> sources)
    {
        const ushort saturation = 60_000;
        const double background = 1_000;
        var pixels = new ushort[width * height];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var value = background + ((x * 17 + y * 31) % 7 - 3);
            foreach (var source in sources)
            {
                var dx = x - source.X;
                var dy = y - source.Y;
                value += source.Amplitude * Math.Exp(-(dx * dx + dy * dy) / (2 * source.Sigma * source.Sigma));
            }
            pixels[y * width + x] = (ushort)Math.Clamp(Math.Round(value), 0, saturation);
        }
        return new MonochromeFrame(width, height, pixels, saturation);
    }
}
