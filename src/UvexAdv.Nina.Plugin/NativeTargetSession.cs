using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

internal interface INativeTargetBoundary
{
    void AttachNativeTarget(NativeTargetSession? session);
    void RecordMeridianSegment(NativeTargetSession session, string runId);
    ClockContinuationBudget CaptureClockContinuationBudget();
    Task<IReadOnlyList<string>> ConfirmSequenceIdleAsync(CancellationToken token, bool requireSettledMotion = true);
    // Only a live owner can attest that cleanup/ledgers are settled. This is
    // eligibility for a later fresh idle check, never physical-stop proof.
    bool CanRecheckSequenceIdle => false;
    Task<GateResult> RecoverMountClockAtIdleBoundaryAsync(ObservationPlan failedPlan, CancellationToken token);
    // The night owner durably reserves its one scan immediately before motion.
    // Null is a no-motion diagnosis; TargetQuality requires a verified stop.
    Task<GateResult?> TryRecoverSevereDefocusAtIdleBoundaryAsync(ObservationPlan failedPlan,
        Func<CancellationToken, Task<bool>> tryReserveScan, CancellationToken token) => Task.FromResult<GateResult?>(null);
}

// Accounting shared by native clock/focus-recovery and meridian segments. No optical
// evidence or device commands live here; every segment uses the canonical runner.
internal class NativeTargetSession(NativeSequencePlan original)
{
    public NativeSequencePlan Original { get; } = original;
    internal AdaptiveGuidingRecovery GuidingRecovery { get; } = new();
    public int Accepted { get; private set; }
    public int Attempts { get; private set; }
    public int ReusedProbes { get; private set; }
    public double AcceptedSeconds { get; private set; }
    public ObservationPlan? TargetPlan { get; set; }
    public List<string> SegmentRunIds { get; } = [];
    internal ClockContinuationBudget? ClockBudget { get; private set; }
    internal string? SharedActionConfigurationSha256 { get; set; }
    internal ClockRecoveryPositionCheck? ClockPositionCheck { get; set; }
    internal bool ClockContinuationUsed { get; private set; }
    internal bool FocusAttempted { get; private set; }
    internal bool FocusContinuationUsed { get; private set; }
    internal bool HasBudgetedContinuation => ClockContinuationUsed || FocusContinuationUsed;

    public NativeSequencePlan RemainingPlan()
    {
        var remaining = Original with
        {
            ScienceFrames = Original.ScienceFrames - Accepted,
            MaximumAttempts = Original.MaximumAttempts - Attempts,
            DeferObservatoryCloseout = true,
            ForceGuideCalibration = Original.ForceGuideCalibration && SegmentRunIds.Count == 0,
        };
        if (HasBudgetedContinuation && TargetPlan is { } plan)
        {
            var targetEnd = plan.PlannedStartUtc + plan.PlannedDuration;
            remaining = remaining with { TargetDeadlineUtc = targetEnd };
        }
        if (remaining.Validate().Count != 0)
            throw new InvalidOperationException("剩余科学帧或尝试预算不足；分段不能重新发放曝光预算。");
        return remaining;
    }

    internal bool TryContinueAfterClockRepair(ClockContinuationBudget budget, DateTimeOffset now)
    {
        if (ClockContinuationUsed || !CanRecoverWithinOriginalBudget(budget, now)) return false;
        ClockContinuationUsed = true;
        ClockBudget = budget;
        return true;
    }

    internal bool TryBeginFocusRecovery(ClockContinuationBudget budget, DateTimeOffset now)
    {
        if (FocusAttempted || !CanRecoverWithinOriginalBudget(budget, now)) return false;
        FocusAttempted = true;
        return true;
    }

    internal bool TryContinueAfterFocusRepair(ClockContinuationBudget budget, DateTimeOffset now)
    {
        if (!FocusAttempted || FocusContinuationUsed || !CanRecoverWithinOriginalBudget(budget, now)) return false;
        FocusContinuationUsed = true;
        ClockBudget = budget;
        return true;
    }

    internal bool CanRecoverWithinOriginalBudget(ClockContinuationBudget budget, DateTimeOffset now) =>
        TargetPlan is not null && Accepted < Original.ScienceFrames &&
        Original.MaximumAttempts - Attempts >= Original.ScienceFrames - Accepted &&
        budget.HasRemainingBudget(now) && now < RecoveryDeadline(budget);

    // Diagnostics and focus motion spend elapsed time too, including original
    // G3/PHD2/fine-acquisition deadlines; no fresh recovery timer replenishes it.
    internal DateTimeOffset RecoveryDeadline(ClockContinuationBudget budget)
    {
        var end = TargetPlan is { } plan ? plan.PlannedStartUtc + plan.PlannedDuration : DateTimeOffset.MinValue;
        if (Original.TargetDeadlineUtc is { } target && target < end) end = target;
        if (Original.DeadlineUtc is { } night && night < end) end = night;
        if (budget.G3 is { } g3) end = Min(end, Deadline(g3.StartedUtc, g3.MaximumSeconds));
        if (budget.Phd2 is { } phd2) end = Min(end, Deadline(phd2.StartedUtc, phd2.MaximumSeconds));
        if (budget.FineStartedUtc is { } fine) end = Min(end, Deadline(fine, budget.FineMaximumSeconds));
        return end;
        static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
        static DateTimeOffset Deadline(DateTimeOffset start, double seconds) =>
            double.IsFinite(seconds) && seconds > 0 && seconds < (DateTimeOffset.MaxValue - start).TotalSeconds
                ? start.AddSeconds(seconds) : DateTimeOffset.MinValue;
    }

    // A later meridian segment must not replenish any continuation's budget.
    internal void RetainClockBudget(ClockContinuationBudget budget)
    {
        if (HasBudgetedContinuation) ClockBudget = budget;
    }

    public void RecordSegment(string runId, int accepted, int attempts, int reused, double seconds)
    {
        if (SegmentRunIds.Contains(runId, StringComparer.Ordinal) || accepted < 0 || attempts < accepted ||
            reused < 0 || reused > accepted || !double.IsFinite(seconds) || seconds < 0 ||
            Accepted + accepted > Original.ScienceFrames || Attempts + attempts > Original.MaximumAttempts)
            throw new InvalidOperationException("目标分段计数无效或重复；禁止重新计入原始帧。");
        SegmentRunIds.Add(runId);
        Accepted += accepted; Attempts += attempts; ReusedProbes += reused; AcceptedSeconds += seconds;
    }

    public ObservationAcquisitionProgress Aggregate(ObservationAcquisitionProgress local) => local with
    {
        RequestedFrames = Original.ScienceFrames, AcceptedFrames = Accepted + local.AcceptedFrames,
        AttemptedFrames = Attempts + local.AttemptedFrames, MaximumAttempts = Original.MaximumAttempts,
        ReusedProbeFrames = ReusedProbes + local.ReusedProbeFrames,
        AcceptedExposureSeconds = AcceptedSeconds + local.AcceptedExposureSeconds,
    };
}
