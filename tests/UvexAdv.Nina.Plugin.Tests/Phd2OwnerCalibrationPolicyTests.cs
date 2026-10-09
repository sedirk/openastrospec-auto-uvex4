using System.Text.RegularExpressions;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class Phd2OwnerCalibrationPolicyTests
{
    [Fact]
    public void AllProductionGuideCommandsLeaveCalibrationToPhd2()
    {
        foreach (var name in new[] { "RealObservationStageRunner.cs", "RealObservationStageRunner.Phd2SlitPlacement.cs" })
        {
            var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", name));
            var calls = Regex.Matches(source, @"phd2\.GuideAndSettleAsync\([\s\S]*?ConfigureAwait\(false\)");
            Assert.NotEmpty(calls.Cast<Match>());
            foreach (Match call in calls) Assert.Contains("forceRecalibration: false", call.Value);
            Assert.DoesNotContain("ShouldRefreshBeforeGuide", source);
            Assert.DoesNotContain("PHD2_CALIBRATION_GEOMETRY_UNSAFE", source);
        }
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
