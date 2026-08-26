namespace UvexAdv.Observatory;

/// <summary>
/// Pure, testable policy checks shared by the N.I.N.A. orchestration boundary.
/// A full real observation is deliberately stricter than a separately supervised
/// manual commissioning operation: every declared safety capability must exist
/// and every physical action must be preceded by a fresh passing gate.
/// </summary>
public static class ObservationAutomationPolicy
{
    public static GateResult AuthorizeExecutionMode(
        bool sequenceRequestsRealMode,
        bool profileAllowsRealMode,
        bool realModeCommissioned)
    {
        if (!sequenceRequestsRealMode)
        {
            return GateResult.Pass(
                "SIMULATOR_MODE_AUTHORIZED",
                "The sequence is explicitly locked to simulator mode and cannot open physical hardware.");
        }

        if (!profileAllowsRealMode)
        {
            return GateResult.Fail(
                "REAL_MODE_NOT_CURRENTLY_AUTHORIZED",
                "This saved sequence requests REAL mode, but the current N.I.N.A. Profile is set to simulator mode. The old real authorization is not reusable.");
        }

        if (!realModeCommissioned)
        {
            return GateResult.Unknown(
                "REAL_MODE_NOT_COMMISSIONED",
                "This sequence requests REAL mode, but the current N.I.N.A. Profile has no commissioned real-hardware authorization.");
        }

        return GateResult.Pass(
            "REAL_MODE_AUTHORIZED",
            "Both the saved sequence and the current commissioned N.I.N.A. Profile explicitly authorize REAL mode.");
    }

    public static GateResult ValidateFullAutomationCapabilities(
        bool requireSafetyMonitor,
        bool requireOpenDomeOrRoof,
        bool requireWeatherData,
        bool requireOpenOpticalCover,
        bool allowWeakSupervision = false)
    {
        var disabled = new List<string>();
        if (!requireSafetyMonitor) disabled.Add("safety monitor");
        if (!requireOpenDomeOrRoof) disabled.Add("open roof/dome state");
        if (!requireWeatherData) disabled.Add("weather data");
        if (!requireOpenOpticalCover) disabled.Add("open optical-cover state");

        if (disabled.Count == 0)
        {
            return GateResult.Pass(
                "FULL_AUTOMATION_CAPABILITIES_REQUIRED",
                "The immutable run requires safety-monitor, roof, weather and optical-cover evidence.");
        }

        return allowWeakSupervision
            ? GateResult.Pass(
                "WEAK_SUPERVISION_CAPABILITIES_DECLARED",
                $"Weak operator supervision explicitly permits missing {string.Join(", ", disabled)}. " +
                "This is not unattended authority; connected adapters that explicitly report an unsafe or closed state still block actions.")
            : GateResult.Unknown(
                "FULL_AUTOMATION_CAPABILITY_DISABLED",
                $"A full unattended REAL run cannot disable {string.Join(", ", disabled)}. Use a separately supervised manual commissioning procedure instead.");
    }

    public static GateResult ValidateLockedPlanSafety(
        ObservationPlan plan,
        MotionLimits expectedMotion,
        bool expectedRequireSafetyMonitor)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(expectedMotion);

        var sameMotion = Same(plan.Motion.MaximumSingleCorrectionDegrees, expectedMotion.MaximumSingleCorrectionDegrees) &&
                         Same(plan.Motion.MaximumCumulativeCorrectionDegrees, expectedMotion.MaximumCumulativeCorrectionDegrees) &&
                         plan.Motion.MaximumCorrectionAttempts == expectedMotion.MaximumCorrectionAttempts &&
                         plan.Motion.EffectiveMaximumAcquisitionTime == expectedMotion.EffectiveMaximumAcquisitionTime;
        if (!sameMotion)
        {
            return GateResult.Fail(
                "PLAN_MOTION_SNAPSHOT_MISMATCH",
                "The manifest ObservationPlan motion limits do not match the immutable real-run configuration.");
        }

        if (plan.RequireSafetyMonitor != expectedRequireSafetyMonitor)
        {
            return GateResult.Fail(
                "PLAN_SAFETY_SNAPSHOT_MISMATCH",
                "The manifest ObservationPlan safety-monitor requirement does not match the immutable real-run configuration.");
        }

        return GateResult.Pass(
            "PLAN_SAFETY_SNAPSHOT_LOCKED",
            "The manifest plan and immutable real-run motion/safety snapshot agree.");
    }

    public static GateResult CombineImmediateActionGates(params GateResult[] gates)
    {
        ArgumentNullException.ThrowIfNull(gates);
        if (gates.Length == 0)
        {
            return GateResult.Unknown(
                "IMMEDIATE_ACTION_GATES_MISSING",
                "No immediate physical-action gates were supplied; the action is prohibited.");
        }

        foreach (var gate in gates)
        {
            if (gate is null)
            {
                return GateResult.Unknown(
                    "IMMEDIATE_ACTION_GATE_NULL",
                    "An immediate physical-action gate was unavailable; the action is prohibited.");
            }
            if (gate.Disposition != GateDisposition.Passed) return gate;
        }

        var metrics = gates
            .Where(gate => gate.Metrics is not null)
            .SelectMany(gate => gate.Metrics!)
            .GroupBy(item => item.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.Ordinal);
        return GateResult.Pass(
            "IMMEDIATE_PHYSICAL_ACTION_GATES_VALID",
            "Safety monitor, roof, weather, horizon, mount UTC and optical cover were freshly revalidated immediately before the physical action.",
            metrics.Count == 0 ? null : metrics);
    }

    private static bool Same(double left, double right) =>
        double.IsFinite(left) &&
        double.IsFinite(right) &&
        Math.Abs(left - right) <= 1e-12 * Math.Max(1, Math.Max(Math.Abs(left), Math.Abs(right)));
}
