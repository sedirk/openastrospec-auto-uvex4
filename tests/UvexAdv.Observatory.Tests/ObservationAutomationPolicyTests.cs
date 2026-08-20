using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Observatory.Tests;

public sealed class ObservationAutomationPolicyTests
{
    [Fact]
    public void SavedRealSequenceRequiresCurrentProfileAuthorization()
    {
        var result = ObservationAutomationPolicy.AuthorizeExecutionMode(
            sequenceRequestsRealMode: true,
            profileAllowsRealMode: false,
            realModeCommissioned: true);

        Assert.Equal(GateDisposition.Failed, result.Disposition);
        Assert.Equal("REAL_MODE_NOT_CURRENTLY_AUTHORIZED", result.Code);
    }

    [Fact]
    public void SimulatorSequenceNeverInheritsHistoricalRealAuthorization()
    {
        var result = ObservationAutomationPolicy.AuthorizeExecutionMode(
            sequenceRequestsRealMode: false,
            profileAllowsRealMode: true,
            realModeCommissioned: true);

        Assert.Equal(GateDisposition.Passed, result.Disposition);
        Assert.Equal("SIMULATOR_MODE_AUTHORIZED", result.Code);
    }

    [Fact]
    public void FullAutomationCannotDisableUnknownRoofOrOtherSafetyCapabilities()
    {
        var result = ObservationAutomationPolicy.ValidateFullAutomationCapabilities(
            requireSafetyMonitor: true,
            requireOpenDomeOrRoof: false,
            requireWeatherData: true,
            requireOpenOpticalCover: true);

        Assert.Equal(GateDisposition.Indeterminate, result.Disposition);
        Assert.Equal("FULL_AUTOMATION_CAPABILITY_DISABLED", result.Code);
        Assert.Contains("roof", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ImmediateActionCompositionFailsClosedOnIndeterminateRoof()
    {
        var result = ObservationAutomationPolicy.CombineImmediateActionGates(
            GateResult.Pass("SAFETY", "safe"),
            GateResult.Unknown("ROOF_STATE_UNKNOWN", "roof unknown"),
            GateResult.Pass("CLOCK", "clock valid"),
            GateResult.Pass("COVER", "cover open"));

        Assert.Equal(GateDisposition.Indeterminate, result.Disposition);
        Assert.Equal("ROOF_STATE_UNKNOWN", result.Code);
    }

    [Fact]
    public void LockedPlanMustUseExactMotionAndSafetySnapshot()
    {
        var expected = new MotionLimits(0.01, 0.03, 4, TimeSpan.FromMinutes(7));
        var plan = CreatePlan(expected, requireSafetyMonitor: true);

        var matching = ObservationAutomationPolicy.ValidateLockedPlanSafety(plan, expected, true);
        var changedMotion = ObservationAutomationPolicy.ValidateLockedPlanSafety(
            plan,
            expected with { MaximumSingleCorrectionDegrees = 0.02 },
            true);
        var changedSafety = ObservationAutomationPolicy.ValidateLockedPlanSafety(plan, expected, false);

        Assert.Equal(GateDisposition.Passed, matching.Disposition);
        Assert.Equal("PLAN_MOTION_SNAPSHOT_MISMATCH", changedMotion.Code);
        Assert.Equal(GateDisposition.Failed, changedMotion.Disposition);
        Assert.Equal("PLAN_SAFETY_SNAPSHOT_MISMATCH", changedSafety.Code);
        Assert.Equal(GateDisposition.Failed, changedSafety.Disposition);
    }

    private static ObservationPlan CreatePlan(MotionLimits motion, bool requireSafetyMonitor) => new(
        "policy-test-run",
        "policy-test-setup",
        new EquatorialTarget("Deneb", "HIP 102098", 310.357979, 45.280338),
        new ObservatorySite(33.375833, 120.416667, 0),
        DateTimeOffset.UtcNow,
        TimeSpan.FromMinutes(3),
        new HorizonPolicy(),
        motion,
        "ATR585M-test",
        "G3M2210M-test",
        "QHYminiCam8M-test",
        requireSafetyMonitor);
}
