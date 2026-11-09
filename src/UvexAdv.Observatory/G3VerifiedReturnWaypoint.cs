namespace UvexAdv.Observatory;

/// <summary>Durable ADR-0051 proof, not a replacement TAN origin or fresh target identity.</summary>
public sealed record G3VerifiedReturnWaypoint(double RaDegrees, double DeclinationDegrees,
    int ChargedAction, DateTimeOffset FrameCompletedUtc, DateTimeOffset VerifiedUtc,
    string FramePath, string FrameSha256, string FormalEvidencePath, string FormalEvidenceSha256,
    string MountBindingSha256);

public static class G3VerifiedReturnWaypointPolicy
{
    // Caller validates immutable current-image formal WCS, its live capture
    // binding and all physical gates. This finite port cannot mint budgets.
    public static G3AcquisitionMotionState? Promote(G3AcquisitionMotionState state,
        G3VerifiedReturnWaypoint waypoint, bool sourceAndDestinationFormal,
        bool freshContextVerified, double priorRemainingArcseconds, double freshRemainingArcseconds)
    {
        if (!sourceAndDestinationFormal || !freshContextVerified || state.Validate().Count != 0 ||
            state.Kind != G3AcquisitionMotionKind.WcsCentering ||
            state.Phase != G3AcquisitionMotionPhase.AwaitingFreshSolve ||
            waypoint.ChargedAction != state.CorrectionAttempts ||
            waypoint.ChargedAction <= (state.VerifiedReturnWaypoint?.ChargedAction ?? 0) ||
            waypoint.FrameCompletedUtc <= state.UpdatedUtc || waypoint.VerifiedUtc < waypoint.FrameCompletedUtc ||
            !double.IsFinite(priorRemainingArcseconds) || !double.IsFinite(freshRemainingArcseconds) ||
            freshRemainingArcseconds < 0 ||
            priorRemainingArcseconds - freshRemainingArcseconds <= state.ArrivalToleranceArcseconds ||
            G3AcquisitionMotionPlanner.AngularSeparationArcseconds(state.PriorReportedRaDegrees,
                state.PriorReportedDeclinationDegrees, waypoint.RaDegrees, waypoint.DeclinationDegrees) > state.ArrivalToleranceArcseconds)
            return null;
        var candidate = state with { VerifiedReturnWaypoint = waypoint, UpdatedUtc = waypoint.VerifiedUtc,
            LastReason = "Fresh formal WCS and stable same-context arrival advanced the return waypoint; initial origin, all debits and original clocks/limits unchanged." };
        return candidate.Validate().Count == 0 ? candidate : null;
    }
}
