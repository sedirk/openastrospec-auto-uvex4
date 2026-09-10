namespace UvexAdv.Qhy.Core;

public sealed record QhyFocusRunPolicy(string DeviceId, int StartPositionSteps,
    int MinimumPositionSteps, int MaximumPositionSteps, int MaximumSingleMoveSteps,
    int MaximumCumulativeMoveSteps, int BacklashCompensationSteps = 0, int ApproachSign = 0);

public sealed record QhyFocusBudgetState(int ExpectedPosition, long CumulativeSteps, bool Pending);

public sealed record QhyFocuserStatus(bool Connected, string DeviceId, int? PositionSteps,
    string? ObservationRunId, bool PositionVerified, DateTimeOffset TimestampUtc);

/// <summary>Endpoint budget for native filter-offset focus. Reserve before calling
/// the owner; an uncertain operation cannot get another allowance on retry.</summary>
public sealed class QhyFocusBudget
{
    private readonly Action<QhyFocusBudgetState> journal;
    public QhyFocusBudget(QhyFocusRunPolicy policy, Action<QhyFocusBudgetState> journal,
        QhyFocusBudgetState? restored = null)
    {
        if (string.IsNullOrWhiteSpace(policy.DeviceId) || policy.MinimumPositionSteps >= policy.MaximumPositionSteps ||
            policy.StartPositionSteps < policy.MinimumPositionSteps || policy.StartPositionSteps > policy.MaximumPositionSteps ||
            policy.MaximumSingleMoveSteps < 0 || policy.MaximumCumulativeMoveSteps < policy.MaximumSingleMoveSteps ||
            policy.BacklashCompensationSteps < 0 || policy.ApproachSign is < -1 or > 1 ||
            (policy.MaximumSingleMoveSteps > 0 && policy.ApproachSign == 0))
            throw new InvalidOperationException("PHOTOMETRY_FOCUS_POLICY_INVALID: Invalid locked focus range, budget or approach direction.");
        Policy = policy; this.journal = journal;
        State = restored ?? new(policy.StartPositionSteps, 0, false);
        if (State.ExpectedPosition < policy.MinimumPositionSteps || State.ExpectedPosition > policy.MaximumPositionSteps ||
            State.CumulativeSteps < 0 || State.CumulativeSteps > policy.MaximumCumulativeMoveSteps)
            throw new InvalidOperationException("PHOTOMETRY_FOCUS_LEDGER_INVALID: Saved budget is inconsistent with the policy.");
    }
    public QhyFocusRunPolicy Policy { get; }
    public QhyFocusBudgetState State { get; private set; }
    public int ExpectedPosition => State.ExpectedPosition;
    public long CumulativeSteps => State.CumulativeSteps;
    public bool Pending => State.Pending;

    public int Reserve(int currentPosition, int offset, int signedOvershoot = 0)
    {
        if (Pending || currentPosition != ExpectedPosition)
            throw new InvalidOperationException("PHOTOMETRY_FOCUS_POSITION_UNCONFIRMED: Native focus position changed or a previous movement is unresolved.");
        var target = checked(currentPosition + offset);
        var intermediate = checked(target + signedOvershoot);
        var amount = Math.Abs((long)offset + signedOvershoot) + Math.Abs((long)signedOvershoot);
        var finalApproach = signedOvershoot == 0 ? Math.Sign(offset) : -Math.Sign(signedOvershoot);
        if ((offset == 0 && signedOvershoot != 0) ||
            (signedOvershoot != 0 && Math.Sign(signedOvershoot) != Math.Sign(offset)) ||
            Math.Abs((long)signedOvershoot) > Policy.BacklashCompensationSteps ||
            (amount > 0 && finalApproach != Policy.ApproachSign))
            throw new InvalidOperationException("PHOTOMETRY_FOCUS_APPROACH_MISMATCH: Native backlash/approach is outside the locked policy.");
        if (target < Policy.MinimumPositionSteps || target > Policy.MaximumPositionSteps ||
            intermediate < Policy.MinimumPositionSteps || intermediate > Policy.MaximumPositionSteps ||
            amount > Policy.MaximumSingleMoveSteps || CumulativeSteps + amount > Policy.MaximumCumulativeMoveSteps)
            throw new InvalidOperationException("PHOTOMETRY_FOCUS_BUDGET_EXHAUSTED: Filter-offset focus exceeds its locked Night Setup bounds.");
        var next = new QhyFocusBudgetState(target, CumulativeSteps + amount, true);
        journal(next); State = next;
        return target;
    }

    public void Confirm(int actualPosition)
    {
        if (!Pending || actualPosition != ExpectedPosition)
            throw new InvalidOperationException("PHOTOMETRY_FOCUS_READBACK_MISMATCH: Native focus endpoint was not confirmed.");
        var next = State with { Pending = false };
        journal(next); State = next;
    }
}

/// <summary>Lease decisions use elapsed monotonic time, independently of UTC
/// clock corrections. Frame timestamps still report their native source.</summary>
public sealed class MonotonicLeaseTimeProvider : TimeProvider
{
    private readonly DateTimeOffset originUtc = DateTimeOffset.UtcNow;
    private readonly long origin = global::System.Diagnostics.Stopwatch.GetTimestamp();
    public override DateTimeOffset GetUtcNow() => originUtc + global::System.Diagnostics.Stopwatch.GetElapsedTime(origin);
}
