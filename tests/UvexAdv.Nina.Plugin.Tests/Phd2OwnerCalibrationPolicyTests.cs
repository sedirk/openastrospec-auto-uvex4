using System.Text.RegularExpressions;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class Phd2OwnerCalibrationPolicyTests
{
    [Fact]
    public void ExplicitOrBoundedAdaptiveRequestUsesOneProductionCalibrationBoundary()
    {
        var explicitRequests = 0;
        foreach (var name in new[] { "RealObservationStageRunner.cs", "RealObservationStageRunner.Phd2SlitPlacement.cs" })
        {
            var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", name));
            var calls = Regex.Matches(source, @"phd2\.GuideAndSettleAsync\([\s\S]*?ConfigureAwait\(false\)");
            Assert.NotEmpty(calls.Cast<Match>());
            foreach (Match call in calls)
            {
                if (call.Value.Contains("forceRecalibration: forceGuideCalibration", StringComparison.Ordinal)) explicitRequests++;
                else Assert.Contains("forceRecalibration: false", call.Value);
            }
            Assert.DoesNotContain("ShouldRefreshBeforeGuide", source);
            Assert.DoesNotContain("PHD2_CALIBRATION_GEOMETRY_UNSAFE", source);
        }
        Assert.Equal(1, explicitRequests);
    }

    [Fact]
    public void ExplicitRequestDefaultsOffConsumesBeforeDispatchAndNeverReissuesDuringRecovery()
    {
        Assert.False(new NativeSequencePlan(2, 4, 120).ForceGuideCalibration);
        var request = new NativeGuideCalibrationRequest();
        Assert.False(request.Consume(false, false));
        Assert.False(request.Consumed);
        Assert.Throws<InvalidOperationException>(() => request.Consume(true, false));
        Assert.False(request.Consumed);
        Assert.True(request.Consume(true, true));
        Assert.True(request.Consumed);
        Assert.False(request.Consume(true, true));
        Assert.False(request.Consume(true, false));
        Assert.True(new NativeGuideCalibrationRequest().Consume(true, true)); // A different target owns a new request.
    }

    [Fact]
    public void FlipContinuationDoesNotRepeatTheTargetsExplicitCalibration()
    {
        var session = new NativeMeridianSession(new NativeSequencePlan(2, 4, 120, ForceGuideCalibration: true), _ => true);
        Assert.True(session.RemainingPlan().ForceGuideCalibration);
        session.RecordSegment("first", 1, 1, 0, 120);
        Assert.False(session.RemainingPlan().ForceGuideCalibration);
        session.RequestAtFrameBoundary(120);
        session.ConfirmFlip();
        Assert.False(session.RemainingPlan().ForceGuideCalibration);
    }

    [Fact]
    public void NativeCalibrationRequiresMotionFreeBoundaryAndFreshReacquisition()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "RealObservationStageRunner.Phd2SlitPlacement.cs"));
        Assert.Contains("pendingPhd2LockShift is null && inheritedSettledBudget is null", source);
        Assert.Contains("lostLockReacquisitionDepth == 0 && postCalibrationReacquisitionDepth == 0", source);
        Assert.Contains("if (settle.NativeCalibrationOccurred)", source);
        Assert.Contains("postCalibrationReacquisitionDepth + 1,", source);
        Assert.Contains("calibrationBefore.CanStartNativeGuide", source);
        Assert.Contains("activeCalibrationBeforeGuide.Status != Phd2ValidationStatus.Valid", source);
        Assert.Contains("Phd2RecoveryCalibrationPresentation.Blocked", source);
    }
}
