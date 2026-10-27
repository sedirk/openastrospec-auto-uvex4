using Xunit;
namespace UvexAdv.Nina.Plugin.Tests;

public sealed class G3ShortHandoffSourceTests
{
    [Fact]
    public void ShortConfirmationIsBoundedSepSequenceWithBothMountBindingsAndNoSolverOrMotion()
    {
        var runner=File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Sources","RealObservationStageRunner.cs"));
        Assert.Contains("shortHandoffConfirmationAttempted = true",runner);
        Assert.Contains("shortExposure <= exposureMilliseconds",runner);
        Assert.Contains("handoffPreset.DirectTargetGuidingExposureMilliseconds",runner);
        Assert.Contains("G3PostWcsShortHandoffPolicy.NeedsSameFrameCoarseSolve(confirmed.Gate)", runner);
        // The same image proceeds to the existing formal solver, but failure
        // must not prematurely suppress the original remaining optical slots.
        Assert.DoesNotContain("if (outsideShortHandoff is not null) return outsideShortHandoff;", runner);
        Assert.Equal(2, runner.Split("retainedOutsideShortHandoff = outsideShortHandoff ?? retainedOutsideShortHandoff;").Length - 1);
        Assert.Contains("!hasOverexposedContent && !allAttemptsWereTransient && retainedOutsideShortHandoff is not null", runner);
        Assert.Contains("RequireIndependentTargetPair = true", runner);
        Assert.Contains("G3MotionPredictionTimingPolicy.Evaluate", runner);
        Assert.Contains("temporal.SearchHintUsable && measuredGeometry", runner);
        Assert.DoesNotContain("if (!handoffPrediction.RequireIndependentTargetPair", runner);
        Assert.Contains("G3PostWcsMeasuredHandoffPolicy.RequiresCoarseSolve(measuredTarget", runner);
        Assert.Contains("G3PostWcsMeasuredHandoffPolicy.OutsideRemainingGate(measuredTarget", runner);
        Assert.Contains("Phd2FreshTargetFluxPolicy.PreserveMeasuredHandoff(", runner);
        Assert.Contains("Phd2FreshTargetFluxPolicy.HasMeasuredHandoffPosition(currentField.TargetIdentification)", runner);
        var source=File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Sources","RealObservationStageRunner.ShortHandoff.cs"));
        Assert.Contains("CaptureG3NativeSingleFrameForAcquisitionAsync",source);
        Assert.Contains("ValidateG3SolveProbeImage",source);
        Assert.Contains("const int shortConfirmationGainPercent = 0", source);
        Assert.Contains("configuration.G3.Binning, shortConfirmationGainPercent", source);
        Assert.Contains("nativeCaptureGainPercent: shortConfirmationGainPercent", source);
        Assert.Contains("captured.GainAndBinningApplied", source);
        Assert.DoesNotContain("SetCameraGain", source);
        Assert.DoesNotContain("SetExposureAsync", source);
        Assert.Contains("independent-sep-short-exposure-target-centre",source);
        Assert.Contains("longMountBinding",source);
        Assert.Equal(3,source.Split("await ValidateG3ProbeMountBindingForMotionAsync").Length-1);
        Assert.Contains("MeasuredPostWcsTarget: shortTarget",source);
        Assert.Contains("if (accepted) return latest",source);
        Assert.Contains("G3SepShortPositionPolicy.Measure",source);
        Assert.Contains("G3ShortPositionMeasurementPolicy.EvaluateConfirmation",source);
        Assert.Contains("attempt <= maximumFrames",source);
        Assert.Contains("G3PostWcsShortHandoffPolicy.Evaluate",source);
        Assert.Contains("RemainingPhd2HandoffRadius(preset, measuredTargetConfirmed: true)", source);
        Assert.Contains("Gate = decision.Gate", source);
        Assert.Contains("Gate = accepted ? probe.Gate : decision.Gate",source);
        Assert.Contains("maximumFrames = attempt + 2",source);
        Assert.Contains("previousHash = previousPath = null",source);
        Assert.Contains("boundaryRecheckUsed ? policy",source);
        Assert.Contains("policy.AfterConfirmation(measurement.Gate.Code, confirmation, attempt)", source);
        Assert.Contains("if (next.ExposureMilliseconds != policy.ExposureMilliseconds)", source);
        Assert.Contains("return latest with { MeasuredPostWcsTarget = null }",source);
        Assert.DoesNotContain("SlitTargetIdentifier.Identify",source);
        Assert.DoesNotContain("SolveImageAsync",source);
        Assert.DoesNotContain("SlewTo",source);
        Assert.DoesNotContain("SetExactLock",source);
    }
}
