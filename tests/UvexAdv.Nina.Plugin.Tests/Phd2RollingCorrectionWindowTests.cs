using UvexAdv.Phd2;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class Phd2RollingCorrectionWindowTests
{
    private static readonly Phd2Point Lock = new(507.08, 940.52);
    private static Phd2SlitFieldMeasurement Sample(int index, double gx, double gy, double tx, double ty) => new(
        index.ToString("X64"), DateTimeOffset.Parse("2026-10-05T17:43:15Z").AddSeconds(index * 2.5),
        new string('A', 64), new(gx, gy), new(tx, ty), new(817.6078455010004, 430.72846992071095),
        500, true, 2000, true, "catalog-wcs:HIP 5447", "saturated topology", 0, "fresh",
        Phd2TargetPositionAuthority.CatalogWcsIdentityWithSaturatedTopologyCentroid, true);

    private static Phd2SlitFieldMeasurement[] Initial() =>
    [
        Sample(0, 505.927, 942.283, 786.8, 411.3),
        Sample(1, 505.641, 942.309, 786.5647976234683, 411.2810991459339),
        Sample(2, 504.276, 941.37, 784.7752380952381, 410.25638095238094),
    ];
    // Actual first sample of the next batch: 01:43:23.490, previously not
    // evaluated until two later samples replaced its latest-frame status.
    private static Phd2SlitFieldMeasurement Next() =>
        Sample(3, 506.563, 941.453, 788.1098085189527, 410.5994529112935);

    private static Phd2CorrectionWindowDecision Evaluate(IReadOnlyList<Phd2SlitFieldMeasurement> window) =>
        Phd2PlacementGuideWindowPolicy.EvaluateCorrectionWindow(window, Lock,
            Phd2SlitGuideMode.OffSlitGuideStar, true, true, false, 2, 2, 3);

    [Fact]
    public void MirachFirstNewFrameRevealsAnAlreadyQualifiedCorrectionOpportunity()
    {
        var initial = Initial();
        Assert.False(Evaluate(initial).CanContinue);
        var window = Phd2PlacementGuideWindowPolicy.AppendConsecutiveSample(initial, Next(), 3, item => item);
        var decision = Evaluate(window);
        Assert.True(decision.CanContinue);
        Assert.InRange(decision.LatestGuideResidualPixels!.Value, 1.066, 1.067);
        Assert.InRange(decision.MaximumEndpointSpreadPixels!.Value, 1.079, 1.080);
        Assert.Equal(initial[1], window[0]);
        Assert.Equal(initial[2], window[1]);
        Assert.Equal(Next(), window[2]);
        Assert.False(Phd2PlacementGuideWindowPolicy.AllWithinTolerance([36.63, 38.69, 35.71], 2));
    }

    [Theory]
    [InlineData(false, true, false, true, 3)]
    [InlineData(true, false, false, true, 3)]
    [InlineData(true, true, true, true, 3)]
    [InlineData(true, true, false, false, 3)]
    [InlineData(true, true, false, true, 2)]
    public void OnlyExistingSupervisedCorrectionWaitCanRoll(bool supervised, bool allowed,
        bool complete, bool waiting, int count) => Assert.False(
            Phd2PlacementGuideWindowPolicy.CanRollCorrectionWindow(supervised,
                Phd2SlitGuideMode.OffSlitGuideStar, allowed, complete, waiting, count, 3));

    [Fact]
    public void DirectGuidingAndIncompleteDeclaredWindowCannotRoll()
    {
        Assert.False(Phd2PlacementGuideWindowPolicy.CanRollCorrectionWindow(true,
            Phd2SlitGuideMode.DegradedDirectTargetGuiding, true, false, true, 3, 3));
        Assert.False(Phd2PlacementGuideWindowPolicy.CanRollCorrectionWindow(true,
            Phd2SlitGuideMode.OffSlitGuideStar, true, false, true, 3, 4));
        Assert.True(Phd2PlacementGuideWindowPolicy.CanRollCorrectionWindow(true,
            Phd2SlitGuideMode.OffSlitGuideStar, true, false, true, 3, 3));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("time")]
    [InlineData("topology")]
    public void InvalidExtensionCannotReuseThePrecedingWindow(string fault)
    {
        var initial = Initial();
        var next = fault switch
        {
            "duplicate" => Next() with { FrameSha256 = initial[0].FrameSha256 },
            "time" => Next() with { CapturedUtc = initial[^1].CapturedUtc },
            _ => Next() with { TopologyFingerprintSha256 = new string('B', 64) },
        };
        Assert.Throws<InvalidOperationException>(() => Phd2PlacementGuideWindowPolicy.AppendConsecutiveSample(
            initial, next, 3, item => item));
    }

    [Fact]
    public void CannotDropAnUnfavourableRecentFrameOrFabricateFullWindow()
    {
        var initial = Initial();
        initial[1] = initial[1] with { TargetCentroid = new(770, 410) };
        var window = Phd2PlacementGuideWindowPolicy.AppendConsecutiveSample(initial, Next(), 3, item => item);
        Assert.Equal("CORRECTION_WINDOW_ENDPOINT_UNSTABLE", Evaluate(window).Code);
        Assert.Throws<InvalidOperationException>(() => Phd2PlacementGuideWindowPolicy.AppendConsecutiveSample(
            initial[..2], Next(), 3, item => item));
    }

    [Fact]
    public void SharedRunnerChecksEpochAndKeepsPostMotionFullWindowAndOriginalBudget()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "RealObservationStageRunner.Phd2SlitPlacement.cs"));
        var convergence = WorkflowSource.Convergence;
        Assert.Contains("requiredCompletionFrames, completionDeadline,", convergence);
        Assert.Contains("cancellationToken, rollingCorrectionWindow ? previousCompletionWindow : null,", convergence);
        Assert.Contains("session.LastMeasurement.Measurement.GuideStar).ConfigureAwait(false)", convergence);
        Assert.Contains("Phd2PlacementGuideWindowPolicy.CaptureConsecutiveWindowAsync(", source);
        Assert.DoesNotContain("newCompletionMeasurements.Single()", convergence);
        Assert.Contains("previousWindowDecision = correctionWindow", convergence);
        Assert.Contains("currentWindowFrames = targetCompletionWindow.Select", convergence);
        Assert.Contains("snapshot.ConnectionEpoch != session.ConnectionEpoch || snapshot.GuideEpoch != session.GuideEpoch", convergence);
        Assert.Contains("targetCompletionWindow = measurements;", convergence);
        Assert.Contains("ledger.StartedUtc + completionLimits.MaximumElapsed", convergence);
        Assert.Contains("var nextAcquisitionBudget = Phd2SlitLockShiftPlanner.EvaluateAcquisitionBudget(", convergence);
    }
}
