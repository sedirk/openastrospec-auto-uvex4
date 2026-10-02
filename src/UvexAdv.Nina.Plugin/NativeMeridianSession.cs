using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

/// <summary>
/// One target, at most one scheduled flip, two immutable acquisition segments.
/// Counts are local to each manifest; only the target presentation is cumulative.
/// Never reuses a FITS, a lock position, or a motion budget from the other side.
/// </summary>
internal sealed class NativeMeridianSession(NativeSequencePlan original, Func<double, bool> isDue)
{
    public NativeSequencePlan Original { get; } = original;
    public int Accepted { get; private set; }
    public int Attempts { get; private set; }
    public int ReusedProbes { get; private set; }
    public double AcceptedSeconds { get; private set; }
    public bool Requested { get; private set; }
    public bool Flipped { get; private set; }
    public ObservationPlan? TargetPlan { get; set; }
    public Action? VerifyScope { get; init; }
    public List<string> SegmentRunIds { get; } = [];

    public bool RequestAtFrameBoundary(double exposureSeconds)
    {
        if (!double.IsFinite(exposureSeconds) || exposureSeconds <= 0)
            throw new InvalidOperationException("翻转边界没有有效的下一帧曝光时长。");
        if (Requested) return true;
        if (!isDue(exposureSeconds)) return false;
        if (Flipped) throw new InvalidOperationException("同一目标再次请求翻转；不自动重复运动，请检查镜筒侧和翻转配置。");
        return Requested = true;
    }

    public NativeSequencePlan RemainingPlan()
    {
        var remaining = Original with
        {
            ScienceFrames = Original.ScienceFrames - Accepted,
            MaximumAttempts = Original.MaximumAttempts - Attempts,
            DeferObservatoryCloseout = true,
        };
        if (remaining.Validate().Count != 0)
            throw new InvalidOperationException("剩余科学帧或尝试预算不足；翻转不能重新发放曝光尝试预算。");
        return remaining;
    }

    public void RecordSegment(string runId, int accepted, int attempts, int reused, double seconds)
    {
        if (SegmentRunIds.Contains(runId, StringComparer.Ordinal) || accepted < 0 || attempts < accepted ||
            reused < 0 || reused > accepted || !double.IsFinite(seconds) || seconds < 0 ||
            Accepted + accepted > Original.ScienceFrames || Attempts + attempts > Original.MaximumAttempts)
            throw new InvalidOperationException("翻转分段计数无效或重复；禁止重新计入原始帧。");
        SegmentRunIds.Add(runId);
        Accepted += accepted; Attempts += attempts; ReusedProbes += reused; AcceptedSeconds += seconds;
    }

    public void ConfirmFlip()
    {
        if (!Requested || Flipped) throw new InvalidOperationException("没有待确认的唯一翻转请求。");
        Requested = false; Flipped = true;
    }

    public ObservationAcquisitionProgress Aggregate(ObservationAcquisitionProgress local) => local with
    {
        RequestedFrames = Original.ScienceFrames,
        AcceptedFrames = Accepted + local.AcceptedFrames,
        AttemptedFrames = Attempts + local.AttemptedFrames,
        MaximumAttempts = Original.MaximumAttempts,
        ReusedProbeFrames = ReusedProbes + local.ReusedProbeFrames,
        AcceptedExposureSeconds = AcceptedSeconds + local.AcceptedExposureSeconds,
    };
}
