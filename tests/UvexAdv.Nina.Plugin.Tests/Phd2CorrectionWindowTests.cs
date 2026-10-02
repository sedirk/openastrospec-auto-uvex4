using UvexAdv.Phd2;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class Phd2CorrectionWindowTests
{
    private static readonly DateTimeOffset Epoch = DateTimeOffset.Parse("2026-10-02T17:26:45Z");
    private static readonly Phd2Point Lock = new(624.59, 1001.25);

    private static Phd2SlitFieldMeasurement Sample(int index, Phd2Point guide, Phd2Point target) => new(
        index.ToString("X64"), Epoch.AddSeconds(index * 3), new string('a', 64), guide, target,
        new(817.5433047323355, 428.8802641221478), 550, true, 2000, true,
        "catalog-wcs:HIP 113327", "SATURATED_TARGET_TOPOLOGY_FLUX_NOT_APPLICABLE", 0, "fresh",
        Phd2TargetPositionAuthority.CatalogWcsIdentityWithSaturatedTopologyCentroid, true);

    // Numeric regression from EW Lac, 01:26:45--50. No raw observing files or
    // machine-local configuration are required by this hardware-free test.
    private static Phd2SlitFieldMeasurement[] EwLacWindow() =>
    [
        Sample(1, new(619.823, 1003.133), new(803.1050228310502, 415.81963470319636)),
        Sample(2, new(623.054, 1003.66), new(805.7440347071583, 417.1583514099783)),
        Sample(3, new(625.273, 1001.682), new(808.1, 415.2603448275862)),
    ];

    private static Phd2CorrectionWindowDecision Evaluate(Phd2SlitFieldMeasurement[] samples,
        bool supervised = true, bool allowed = true, bool complete = false,
        Phd2SlitGuideMode mode = Phd2SlitGuideMode.OffSlitGuideStar,
        double guideTolerance = 2, double endpointTolerance = 2, int requiredFrames = 3) =>
        Phd2PlacementGuideWindowPolicy.EvaluateCorrectionWindow(samples, Lock, mode,
            supervised, allowed, complete, guideTolerance, endpointTolerance, requiredFrames);

    [Fact]
    public void EwLacCoherentGeometryCanContinueButCannotProveSlitCompletion()
    {
        var samples = EwLacWindow();
        var decision = Evaluate(samples);
        Assert.True(decision.CanContinue);
        Assert.InRange(decision.LatestGuideResidualPixels!.Value, 0.80, 0.82);
        Assert.InRange(decision.MaximumEndpointSpreadPixels!.Value, 0.99, 1.02);
        Assert.True(Phd2PlacementGuideWindowPolicy.MustWaitForExistingLock(
            [5.1283, 2.8578, 0.8082], 2, true, true, "SLIT_LOCK_STAGE_ALLOWED"));
        double[] residuals = [19.4693, 16.6329, 16.5742];
        Assert.False(Phd2PlacementGuideWindowPolicy.AllWithinTolerance(residuals, 2));
        Assert.False(Phd2PlacementGuideWindowPolicy.CanProbeWithPrecisionWarning(true, true, residuals, 2, 2, 100));
    }

    [Fact]
    public void AnUnfinishedCurrentLockStillWaitsEvenWhenEndpointIsCoherent()
    {
        var samples = EwLacWindow();
        samples[2] = samples[2] with
        {
            GuideStar = new(samples[2].GuideStar.X + 12, samples[2].GuideStar.Y),
            TargetCentroid = new(samples[2].TargetCentroid.X + 12, samples[2].TargetCentroid.Y),
        };
        Assert.Equal("CORRECTION_WINDOW_LOCK_NOT_REACHED", Evaluate(samples).Code);
    }

    [Theory]
    [InlineData(false, true, false)] // no explicit supervision
    [InlineData(true, false, false)] // a denied safety/identity/budget plan never becomes allowed
    [InlineData(true, true, true)] // completion remains a separate all-frame decision
    public void DoesNotOverrideOriginalAuthority(bool supervised, bool allowed, bool complete) =>
        Assert.False(Evaluate(EwLacWindow(), supervised, allowed, complete).CanContinue);

    [Fact]
    public void DirectTargetModeCannotBorrowOffSlitDifferentialGeometry() =>
        Assert.False(Evaluate(EwLacWindow(), mode: Phd2SlitGuideMode.DegradedDirectTargetGuiding).CanContinue);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void NoFrameMayBeDiscardedToHideAnInconsistentEndpoint(int index)
    {
        var samples = EwLacWindow();
        samples[index] = samples[index] with
        {
            TargetCentroid = new(samples[index].TargetCentroid.X + 6, samples[index].TargetCentroid.Y),
        };
        Assert.Equal("CORRECTION_WINDOW_ENDPOINT_UNSTABLE", Evaluate(samples).Code);
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("projection")]
    [InlineData("unmeasured-guide")]
    [InlineData("topology")]
    [InlineData("duplicate")]
    [InlineData("time")]
    [InlineData("nan")]
    [InlineData("infinite-guide")]
    [InlineData("infinite-slit")]
    public void AnyBadFrameRevokesTheContinuation(string invalid)
    {
        var samples = EwLacWindow();
        samples[1] = invalid switch
        {
            "identity" => samples[1] with { TargetIdentityConfirmed = false },
            "projection" => samples[1] with { TargetPositionAuthority = Phd2TargetPositionAuthority.CatalogWcsProjection },
            "unmeasured-guide" => samples[1] with { GuidePositionMeasuredInFrame = false },
            "topology" => samples[1] with { TopologyFingerprintSha256 = new string('b', 64) },
            "duplicate" => samples[1] with { FrameSha256 = samples[0].FrameSha256 },
            "time" => samples[1] with { CapturedUtc = samples[0].CapturedUtc },
            "nan" => samples[1] with { TargetCentroid = new(double.NaN, 0) },
            "infinite-guide" => samples[1] with { GuideStar = new(double.PositiveInfinity, 0) },
            _ => samples[1] with { RecognizedSlitAcquisitionPoint = new(0, double.NegativeInfinity) },
        };
        Assert.False(Evaluate(samples).CanContinue);
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(2, 0)]
    [InlineData(double.NaN, 2)]
    [InlineData(2, double.PositiveInfinity)]
    public void InvalidLimitsFailClosed(double guideTolerance, double endpointTolerance) =>
        Assert.False(Evaluate(EwLacWindow(), guideTolerance: guideTolerance, endpointTolerance: endpointTolerance).CanContinue);

    [Fact]
    public void RequiresFullDeclaredWindow()
    {
        Assert.False(Evaluate(EwLacWindow()[..2]).CanContinue);
        Assert.False(Evaluate(EwLacWindow(), requiredFrames: 4).CanContinue);
    }
}
