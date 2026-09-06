using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class Phd2FreshSlitTemporalConsensusPolicyTests
{
    private static readonly SlitGeometry Seed = new(
        "same-run-led-slit",
        new PixelPoint(817.4691739788824, 426.7574370902117),
        -2,
        410,
        2.5,
        0.5,
        "g3-stable-id",
        1,
        1);

    [Fact]
    public void ThreeObservedVegaFramesPassWithoutLoweringTheSingleFrameGate()
    {
        var samples = new[]
        {
            Sample(1, 2.827858802307179, 3, 0),
            Sample(2, 2.5840133237372913, 1, -1),
            Sample(3, 2.762392958672575, 2, 0),
        };

        var result = Phd2FreshSlitTemporalConsensusPolicy.Evaluate(
            samples,
            Seed,
            singleFrameThresholdSigma: 3,
            maximumMountSpanArcseconds: 2,
            sameRunSlitSeedAuthorized: true,
            guideEpochAndLockStayedFixed: true);

        Assert.Equal(GateDisposition.Passed, result.Gate.Disposition);
        Assert.Equal("PHD2_FRESH_SLIT_TEMPORAL_CONSENSUS", result.Gate.Code);
        Assert.NotNull(result.Detection);
        Assert.Equal(2.5840133237372913, result.MinimumMemberContrastSigma, 12);
        Assert.Equal(2.762392958672575, result.MedianMemberContrastSigma, 12);
        Assert.InRange(result.MaximumPointSpanPixels, 2, 2.01);
        Assert.Equal(1, result.AngleSpanDegrees, 12);
        Assert.All(samples, sample => Assert.Equal(GateDisposition.Indeterminate, sample.Detection.Gate.Disposition));
    }

    [Theory]
    [InlineData(false, true, "PHD2_FRESH_SLIT_CONSENSUS_SEED_UNAUTHORIZED")]
    [InlineData(true, false, "PHD2_FRESH_SLIT_CONSENSUS_GUIDE_EPOCH_CHANGED")]
    public void SameRunSlitIdentityAndUnchangedGuideEpochAreMandatory(
        bool seedAuthorized,
        bool guideEpochStable,
        string expectedCode)
    {
        var result = Phd2FreshSlitTemporalConsensusPolicy.Evaluate(
            StableSamples(),
            Seed,
            3,
            2,
            seedAuthorized,
            guideEpochStable);

        Assert.Equal(GateDisposition.Indeterminate, result.Gate.Disposition);
        Assert.Equal(expectedCode, result.Gate.Code);
        Assert.Null(result.Detection);
    }

    [Fact]
    public void OneWeakMemberCannotBeHiddenByTwoBetterFrames()
    {
        var samples = new[]
        {
            Sample(1, 2.9, 2, 0),
            Sample(2, 2.49, 2, 0),
            Sample(3, 2.9, 2, 0),
        };

        var result = Evaluate(samples);

        Assert.Equal(GateDisposition.Indeterminate, result.Gate.Disposition);
        Assert.Equal("PHD2_FRESH_SLIT_CONSENSUS_NOT_ESTABLISHED", result.Gate.Code);
    }

    [Fact]
    public void DetectorLocusDisagreementCannotAuthorizePlacement()
    {
        var samples = new[]
        {
            Sample(1, 2.8, 1, 0),
            Sample(2, 2.8, 2, 0),
            Sample(3, 2.8, 4, 0),
        };

        var result = Evaluate(samples);

        Assert.Equal(GateDisposition.Indeterminate, result.Gate.Disposition);
        Assert.Equal("PHD2_FRESH_SLIT_CONSENSUS_NOT_ESTABLISHED", result.Gate.Code);
        Assert.True(result.MaximumPointSpanPixels > Phd2FreshSlitTemporalConsensusPolicy.MaximumPointSpanPixels);
    }

    [Fact]
    public void DuplicateFrameHashCannotPretendToBeTemporalEvidence()
    {
        var first = Sample(1, 2.8, 2, 0);
        var duplicate = Sample(2, 2.8, 2, 0, hash: first.FrameSha256);
        var samples = new[] { first, duplicate, Sample(3, 2.8, 2, 0) };

        var result = Evaluate(samples);

        Assert.Equal(GateDisposition.Indeterminate, result.Gate.Disposition);
        Assert.Equal("PHD2_FRESH_SLIT_CONSENSUS_FRAME_IDENTITY_INVALID", result.Gate.Code);
    }

    [Fact]
    public void CaptureStateMutationCannotAuthorizePlacement()
    {
        var samples = StableSamples().ToArray();
        samples[1] = samples[1] with { ExposureChanged = true };

        var result = Evaluate(samples);

        Assert.Equal(GateDisposition.Indeterminate, result.Gate.Disposition);
        Assert.Equal("PHD2_FRESH_SLIT_CONSENSUS_CAPTURE_MUTATED", result.Gate.Code);
    }

    private static IReadOnlyList<Phd2FreshSlitTemporalSample> StableSamples() =>
        new[]
        {
            Sample(1, 2.8, 1, 0),
            Sample(2, 2.75, 2, -1),
            Sample(3, 2.7, 3, 0),
        };

    private static Phd2FreshSlitTemporalConsensus Evaluate(IReadOnlyList<Phd2FreshSlitTemporalSample> samples) =>
        Phd2FreshSlitTemporalConsensusPolicy.Evaluate(
            samples,
            Seed,
            singleFrameThresholdSigma: 3,
            maximumMountSpanArcseconds: 2,
            sameRunSlitSeedAuthorized: true,
            guideEpochAndLockStayedFixed: true);

    private static Phd2FreshSlitTemporalSample Sample(
        int ordinal,
        double contrast,
        double offset,
        double angleOffset,
        string? hash = null)
    {
        hash ??= ordinal.ToString("X64");
        var angle = (Seed.AngleDegrees + angleOffset) * Math.PI / 180d;
        var geometry = Seed with
        {
            AcquisitionPoint = new PixelPoint(
                Seed.AcquisitionPoint.X - Math.Sin(angle) * offset,
                Seed.AcquisitionPoint.Y + Math.Cos(angle) * offset),
            AngleDegrees = Seed.AngleDegrees + angleOffset,
            UncertaintyPixels = 1,
        };
        var detection = new SlitLocusDetection(
            GateResult.Unknown(
                "SLIT_LOCUS_LOW_CONFIDENCE",
                $"Best dark-line contrast is only {contrast:F2} sigma."),
            geometry,
            contrast,
            offset,
            angleOffset);
        var completed = new DateTimeOffset(2026, 9, 4, 13, 40, ordinal * 2, TimeSpan.Zero);
        var binding = G3FieldMountBinding.Create(
            "run-id",
            new string('A', 64),
            new string('B', 64),
            $"C:\\evidence\\frame-{ordinal}.fit",
            hash,
            completed,
            new G3FrameMountReadback(
                279.23977705,
                38.79037337,
                "J2000",
                "PierEast",
                completed.AddMilliseconds(1)));
        return new Phd2FreshSlitTemporalSample(
            hash,
            TriggerGuideFrame: ordinal,
            EventSequence: 100 + ordinal,
            GuideStepUtc: completed,
            GuidingWasInterrupted: false,
            ExposureChanged: false,
            CaptureLoopStarted: false,
            binding,
            detection);
    }
}
