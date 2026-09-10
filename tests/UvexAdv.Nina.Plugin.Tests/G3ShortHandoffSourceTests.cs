using Xunit;
namespace UvexAdv.Nina.Plugin.Tests;

public sealed class G3ShortHandoffSourceTests
{
    [Fact]
    public void ShortConfirmationIsSingleBoundedNativeCaptureWithBothMountBindingsAndNoSolverOrMotion()
    {
        var runner=File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Sources","RealObservationStageRunner.cs"));
        Assert.Contains("shortHandoffConfirmationAttempted = true",runner);
        Assert.Contains("shortExposure < exposureMilliseconds",runner);
        Assert.Contains("handoffPreset.DirectTargetGuidingExposureMilliseconds",runner);
        var source=File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Sources","RealObservationStageRunner.ShortHandoff.cs"));
        Assert.Contains("CaptureG3NativeSingleFrameForAcquisitionAsync",source);
        Assert.Contains("ValidateG3SolveProbeImage",source);
        Assert.Contains("const int shortConfirmationGainPercent = 0", source);
        Assert.Contains("configuration.G3.Binning, shortConfirmationGainPercent", source);
        Assert.Contains("nativeCaptureGainPercent: shortConfirmationGainPercent", source);
        Assert.Contains("captured.GainAndBinningApplied", source);
        Assert.DoesNotContain("SetCameraGain", source);
        Assert.DoesNotContain("SetExposureAsync", source);
        Assert.Contains("independent-short-exposure-target-centre",source);
        Assert.Contains("longMountBinding",source);
        Assert.Equal(3,source.Split("await ValidateG3ProbeMountBindingForMotionAsync").Length-1);
        Assert.Contains("MeasuredPostWcsTarget: shortTarget",source);
        Assert.Contains("return accepted ? probe with",source);
        Assert.DoesNotContain("SolveImageAsync",source);
        Assert.DoesNotContain("SlewTo",source);
        Assert.DoesNotContain("SetExactLock",source);
    }
}
