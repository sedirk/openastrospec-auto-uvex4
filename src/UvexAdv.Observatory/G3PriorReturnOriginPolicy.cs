namespace UvexAdv.Observatory;

public sealed record G3OriginReadback(
    DateTimeOffset CapturedUtc, double RaDegrees, double DeclinationDegrees,
    string CoordinateEpoch, string PierSide, bool ConnectedAndIdle);

/// <summary>
/// Read-only closure of a previous run's final absolute return, not permission
/// to execute an old tangent-plane vector on a different pier side.
/// </summary>
public static class G3PriorReturnOriginPolicy
{
    public static bool IsCandidate(G3AcquisitionMotionState state, string newRunId,
        ObservationStage stage, string currentSide) =>
        stage == ObservationStage.ValidateNightSetup &&
        !string.IsNullOrWhiteSpace(newRunId) &&
        !string.Equals(state.ObservationRunId, newRunId, StringComparison.Ordinal) &&
        state.Phase == G3AcquisitionMotionPhase.ReturnIntent &&
        KnownSide(state.PierSide) && KnownSide(currentSide) &&
        !string.Equals(state.PierSide, currentSide, StringComparison.Ordinal) &&
        Separation(state.OriginRaDegrees, state.OriginDeclinationDegrees,
            state.CommandedRaDegrees, state.CommandedDeclinationDegrees) <= 1e-6;

    public static GateResult Evaluate(G3AcquisitionMotionState state, string newRunId,
        ObservationStage stage, IReadOnlyList<G3OriginReadback> samples,
        double freshSolveToleranceArcseconds, double settleSeconds,
        double maximumDriftArcseconds, DateTimeOffset nowUtc)
    {
        const string code = "G3_MOTION_CROSS_PIER_ORIGIN_UNCONFIRMED";
        var tolerance = G3AcquisitionMotionPlanner.ComputeStableNearOriginToleranceArcseconds(
            state, freshSolveToleranceArcseconds);
        if (state.Validate().Count != 0 || samples.Count < 3 ||
            !IsCandidate(state, newRunId, stage, samples[0].PierSide) ||
            !double.IsFinite(tolerance) || tolerance <= 0 ||
            !double.IsFinite(settleSeconds) || settleSeconds <= 0 ||
            !double.IsFinite(maximumDriftArcseconds) || maximumDriftArcseconds <= 0 ||
            maximumDriftArcseconds > state.ArrivalToleranceArcseconds)
            return GateResult.Unknown(code, "Only a prior run's final absolute-origin return can be closed by a complete read-only stability window; no old motion was dispatched.");

        double maxResidual = 0, maxDrift = 0;
        for (var i = 0; i < samples.Count; i++)
        {
            var sample = samples[i];
            if (!sample.ConnectedAndIdle ||
                !string.Equals(sample.PierSide, samples[0].PierSide, StringComparison.Ordinal) ||
                !string.Equals(sample.CoordinateEpoch, state.CoordinateEpoch, StringComparison.Ordinal) ||
                sample.CapturedUtc < state.UpdatedUtc || sample.CapturedUtc > nowUtc ||
                (i > 0 && sample.CapturedUtc <= samples[i - 1].CapturedUtc))
                return GateResult.Unknown(code, "Mount connection, idle state, current pier side, epoch or readback timestamps changed during the origin check; the old return remains outstanding.");
            var residual = Separation(state.OriginRaDegrees, state.OriginDeclinationDegrees,
                sample.RaDegrees, sample.DeclinationDegrees);
            if (!double.IsFinite(residual))
                return GateResult.Unknown(code, "The owner returned invalid absolute coordinates; the old return remains outstanding.");
            maxResidual = Math.Max(maxResidual, residual);
            for (var j = 0; j < i; j++)
                maxDrift = Math.Max(maxDrift, Separation(samples[j].RaDegrees,
                    samples[j].DeclinationDegrees, sample.RaDegrees, sample.DeclinationDegrees));
        }
        var duration = (samples[^1].CapturedUtc - samples[0].CapturedUtc).TotalSeconds;
        var metrics = new Dictionary<string, double>
        {
            ["originResidualArcseconds"] = maxResidual,
            ["originToleranceArcseconds"] = tolerance,
            ["readbackDriftArcseconds"] = maxDrift,
            ["readbackDriftLimitArcseconds"] = maximumDriftArcseconds,
            ["originReadbackSeconds"] = duration,
            ["originReadbackCount"] = samples.Count,
        };
        if (duration < settleSeconds || (nowUtc - samples[^1].CapturedUtc).TotalSeconds > 2 ||
            maxResidual > tolerance || !double.IsFinite(maxDrift) || maxDrift > maximumDriftArcseconds)
            return GateResult.Unknown(code,
                $"Prior return is on a different pier side. Read-only origin residual {maxResidual:F2} arcsec (limit {tolerance:F2}), drift {maxDrift:F2} arcsec (limit {maximumDriftArcseconds:F2}), window {duration:F2}s (required {settleSeconds:F2}s). No old return command was sent.", metrics);
        return GateResult.Pass("G3_MOTION_PRIOR_ORIGIN_READBACK_CONFIRMED",
            "The prior final absolute return is already at its saved origin with stable owner readbacks. Only that old return is closed; no optical target, old vector or new motion is authorized.", metrics);
    }

    public static G3AcquisitionMotionState CloseVerifiedReturn(G3AcquisitionMotionState state,
        string newRunId, ObservationStage stage, IReadOnlyList<G3OriginReadback> samples,
        double freshSolveToleranceArcseconds, double settleSeconds,
        double maximumDriftArcseconds, DateTimeOffset nowUtc)
    {
        var gate = Evaluate(state, newRunId, stage, samples, freshSolveToleranceArcseconds,
            settleSeconds, maximumDriftArcseconds, nowUtc);
        if (gate.Disposition != GateDisposition.Passed) throw new InvalidOperationException(gate.Message);
        var last = samples[^1];
        var (ra, dec) = G3AcquisitionMotionPlanner.SignedTangentOffsetArcseconds(
            state.OriginRaDegrees, state.OriginDeclinationDegrees, last.RaDegrees, last.DeclinationDegrees);
        return state with
        {
            Phase = G3AcquisitionMotionPhase.SettledBudgetLedger,
            PriorReportedRaDegrees = last.RaDegrees,
            PriorReportedDeclinationDegrees = last.DeclinationDegrees,
            CurrentRaTangentOffsetArcseconds = ra,
            CurrentDeclinationOffsetArcseconds = dec,
            CommandMagnitudeArcseconds = 0,
            UpdatedUtc = nowUtc,
            LastReason = $"Prior final absolute-origin return verified without motion on current side '{last.PierSide}'. Old side '{state.PierSide}', run, origin, charged counters and clock retained; fresh acquisition is required in the new run.",
        };
    }

    private static bool KnownSide(string side) => side is "pierEast" or "pierWest";

    private static double Separation(double ra1, double dec1, double ra2, double dec2)
    {
        if (!double.IsFinite(ra1) || !double.IsFinite(ra2) ||
            !double.IsFinite(dec1) || !double.IsFinite(dec2) ||
            ra1 is < 0 or >= 360 || ra2 is < 0 or >= 360 ||
            dec1 is < -90 or > 90 || dec2 is < -90 or > 90) return double.NaN;
        var radians = Math.PI / 180;
        var a = Math.Pow(Math.Sin((dec2 - dec1) * radians / 2), 2) +
            Math.Cos(dec1 * radians) * Math.Cos(dec2 * radians) *
            Math.Pow(Math.Sin((ra2 - ra1) * radians / 2), 2);
        return 2 * Math.Asin(Math.Sqrt(Math.Clamp(a, 0, 1))) / radians * 3600;
    }
}
