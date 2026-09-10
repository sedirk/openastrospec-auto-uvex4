using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class Phd2FreshSlitStackPolicyTests
{
    private static readonly SlitGeometry Seed = new("fresh-led", new PixelPoint(64, 40),
        0, 100, 2.25, 0.5, "G3", 1, 1);
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 16, 0, 10, TimeSpan.Zero);

    [Fact]
    public void TwoSecondGuideCadenceUsesNewestFrameAgeAndSeparateBoundedWindow()
    {
        var samples = Samples(); // Oldest 6 s old, newest 2 s old.
        var result = Phd2FreshSlitStackPolicy.Evaluate(samples, Seed, 3, 2, 1, 2,
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), Now, true, true);
        Assert.Equal(GateDisposition.Passed, result.Gate.Disposition);
        var staleNewest = Phd2FreshSlitStackPolicy.Evaluate(samples, Seed, 3, 2, 1, 2,
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), Now.AddSeconds(4), true, true);
        Assert.Equal("PHD2_FRESH_SLIT_STACK_LATEST_FRAME_STALE", staleNewest.Gate.Code);
        var wideWindow = Phd2FreshSlitStackPolicy.Evaluate(samples, Seed, 3, 2, 1, 2,
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(3), Now, true, true);
        Assert.NotEqual(GateDisposition.Passed, wideWindow.Gate.Disposition);
    }

    [Fact]
    public void FreshStackCanRecoverTheDarkApertureWithoutEditingInputs()
    {
        var samples = Samples();
        var originals = samples.Select(sample => sample.Frame[64, 40]).ToArray();
        var result = Evaluate(samples);
        Assert.Equal(GateDisposition.Passed, result.Gate.Disposition);
        Assert.Equal("PHD2_FRESH_SLIT_STACK_DETECTED", result.Gate.Code);
        Assert.True(result.ContrastSigma >= 3);
        Assert.Equal(originals, samples.Select(sample => sample.Frame[64, 40]).ToArray());
        Assert.InRange(result.Geometry.AcquisitionPoint.Y, 38, 42);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void SlitIdentityAndGuideEpochRemainRequired(bool seedAuthorized, bool guideFixed)
    {
        Assert.Equal("PHD2_FRESH_SLIT_STACK_CONTEXT_INVALID",
            Evaluate(Samples(), seedAuthorized, guideFixed).Gate.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void PartialWindowsDoNotInventTemporalSupport(int count)
    {
        Assert.Equal("PHD2_FRESH_SLIT_STACK_FRAME_COUNT", Evaluate(Samples().Take(count).ToArray()).Gate.Code);
    }

    [Fact]
    public void ReusedPixelsAndHashesCannotBecomeThreeFrames()
    {
        var samples = Samples();
        samples[1] = samples[0];
        Assert.NotEqual(GateDisposition.Passed, Evaluate(samples).Gate.Disposition);
    }

    [Fact]
    public void ExposureMutationOrUnrelatedFailureCannotUseStackRecovery()
    {
        var samples = Samples();
        samples[1] = samples[1] with { Evidence = samples[1].Evidence with { ExposureChanged = true } };
        Assert.NotEqual(GateDisposition.Passed, Evaluate(samples).Gate.Disposition);
        samples = Samples();
        samples[1] = samples[1] with { Evidence = samples[1].Evidence with
        {
            Detection = samples[1].Evidence.Detection with { Gate = GateResult.Unknown("TARGET_UNKNOWN", "not a slit contrast miss") },
        } };
        Assert.NotEqual(GateDisposition.Passed, Evaluate(samples).Gate.Disposition);
    }

    [Fact]
    public void DifferentRunOrTamperedMountBindingIsNotAccepted()
    {
        var samples = Samples();
        samples[1] = samples[1] with { Evidence = samples[1].Evidence with
        {
            MountBinding = samples[1].Evidence.MountBinding with { ObservationRunId = "another-run" },
        } };
        Assert.NotEqual(GateDisposition.Passed, Evaluate(samples).Gate.Disposition);
    }

    [Theory]
    [InlineData(-60)]
    [InlineData(20)]
    public void StaleOrFutureSamplesCannotAuthorizeAStack(int timeShift)
    {
        var samples = Samples();
        samples[1] = samples[1] with { Evidence = samples[1].Evidence with
        {
            GuideStepUtc = samples[1].Evidence.GuideStepUtc.AddSeconds(timeShift),
        } };
        Assert.NotEqual(GateDisposition.Passed, Evaluate(samples).Gate.Disposition);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(4095)]
    public void BlankOrClippedFramesCannotInventADarkAperture(ushort value)
    {
        var blank = new MonochromeFrame(128, 80, Enumerable.Repeat(value, 128 * 80).ToArray(), 4095);
        var samples = Samples().Select(sample => sample with { Frame = blank }).ToArray();
        Assert.NotEqual(GateDisposition.Passed, Evaluate(samples).Gate.Disposition);
    }

    [Fact]
    public void ProductionUsesLedGeometryAndKeepsCurrentIndividualTargetAndGuideMeasurements()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources",
            "RealObservationStageRunner.Phd2SlitPlacement.cs"));
        Assert.DoesNotContain("Phd2FreshSlitStackPolicy.Evaluate(recentSlitStackFrames", source);
        Assert.Contains("G3RunLedSlitGeometryPolicy.Evaluate", source);
        Assert.Contains("slitGeometryAuthority = \"RUN_LED_OFF_ON_OFF\"", source);
        Assert.Contains("var target = ToPhd2Domain(targetLocal, preset)", source);
        Assert.Equal(5, Phd2FreshSlitFrameRetryPolicy.MaximumCaptureAttempts(3));
    }

    private static SlitLocusDetection Evaluate(Phd2FreshSlitStackSample[] samples,
        bool seedAuthorized = true, bool guideFixed = true) =>
        Phd2FreshSlitStackPolicy.Evaluate(samples, Seed, 3, 2, 1, 2,
            TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(10), Now, seedAuthorized, guideFixed);

    private static Phd2FreshSlitStackSample[] Samples() => Enumerable.Range(1, 3).Select(ordinal =>
    {
        var pixels = new ushort[128 * 80];
        for (var y = 0; y < 80; y++)
        for (var x = 0; x < 128; x++)
        {
            var value = 100 + 4 * x + ((x * 7 + y * 3 + ordinal) % 5);
            if (Math.Abs(y - 40) <= 1) value -= 10;
            pixels[y * 128 + x] = (ushort)value;
        }
        var completed = Now.AddSeconds(-8 + ordinal * 2);
        var hash = ordinal.ToString("X64");
        var binding = G3FieldMountBinding.Create("run", new string('A', 64), new string('B', 64),
            $"C:\\evidence\\{ordinal}.fit", hash, completed,
            new G3FrameMountReadback(339.8, 39.05, "J2000", "PierEast", completed.AddMilliseconds(1)));
        return new Phd2FreshSlitStackSample(new Phd2FreshSlitTemporalSample(hash, ordinal,
            100 + ordinal, completed, false, false, false, binding,
            new SlitLocusDetection(GateResult.Unknown("SLIT_LOCUS_LOW_CONFIDENCE", "single-frame contrast"),
                Seed, 2.6, 0, 0)), new MonochromeFrame(128, 80, pixels, 4095));
    }).ToArray();
}
