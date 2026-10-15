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
}

// Accounting shared by native clock-recovery and meridian segments. No optical
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
    internal bool ClockContinuationUsed { get; private set; }

    public NativeSequencePlan RemainingPlan()
    {
        var remaining = Original with
        {
            ScienceFrames = Original.ScienceFrames - Accepted,
            MaximumAttempts = Original.MaximumAttempts - Attempts,
            DeferObservatoryCloseout = true,
            ForceGuideCalibration = Original.ForceGuideCalibration && SegmentRunIds.Count == 0,
        };
        if (ClockContinuationUsed && TargetPlan is { } plan)
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
        if (ClockContinuationUsed || TargetPlan is null || Accepted >= Original.ScienceFrames ||
            Original.MaximumAttempts - Attempts < Original.ScienceFrames - Accepted ||
            now >= TargetPlan.PlannedStartUtc + TargetPlan.PlannedDuration ||
            Original.DeadlineUtc is { } end && now >= end || !budget.HasRemainingBudget(now)) return false;
        ClockContinuationUsed = true;
        ClockBudget = budget;
        return true;
    }

    // A later meridian segment must not replenish a clock continuation's budget.
    internal void RetainClockBudget(ClockContinuationBudget budget)
    {
        if (ClockContinuationUsed) ClockBudget = budget;
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
