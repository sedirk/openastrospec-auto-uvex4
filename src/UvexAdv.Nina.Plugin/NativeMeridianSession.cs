using UvexAdv.Observatory;
using NINA.Core.Enum;

namespace UvexAdv.Nina.Plugin;

/// <summary>
/// One target, at most one scheduled flip; immutable acquisition segments.
/// Counts are local to each manifest; only the target presentation is cumulative.
/// Never reuses a FITS or lock position from the other side. An earlier clock
/// continuation retains its numeric debits across this boundary (ADR-0035).
/// </summary>
internal sealed class NativeMeridianSession(NativeSequencePlan original, Func<double, bool> isDue) : NativeTargetSession(original)
{
    public bool Requested { get; private set; }
    public bool Flipped { get; private set; }
    public bool ObservedSideChange { get; private set; }
    public PierSide? BeforeSide { get; private set; }
    public PierSide? AfterSide { get; private set; }
    private PierSide? segmentSide;
    public Action? VerifyScope { get; init; }

    internal bool RequestAtOperationBoundary(PierSide side, double operationSeconds = 0,
        bool destinationWouldChangeSide = false, PierSide? savedSide = null, bool allowScheduled = true)
    {
        if (side is not (PierSide.pierEast or PierSide.pierWest) ||
            !double.IsFinite(operationSeconds) || operationSeconds < 0)
            throw new InvalidOperationException("中天交接需要已知侧别和有效动作时长。");
        if (Requested) return true;
        segmentSide ??= savedSide is PierSide.pierEast or PierSide.pierWest ? savedSide : side;
        if (side != segmentSide)
        {
            if (Flipped) throw new InvalidOperationException("本目标已经换侧一次；拒绝重复翻转或反复重发取场预算。");
            BeforeSide = segmentSide;
            AfterSide = side;
            ObservedSideChange = true;
            return Requested = true;
        }
        if (!allowScheduled || (!destinationWouldChangeSide && !isDue(operationSeconds))) return false;
        if (Flipped) throw new InvalidOperationException("本目标再次要求翻转；没有新增翻转预算。");
        BeforeSide = side;
        return Requested = true;
    }

    public bool RequestAtFrameBoundary(double exposureSeconds)
    {
        if (!double.IsFinite(exposureSeconds) || exposureSeconds <= 0)
            throw new InvalidOperationException("翻转边界没有有效的下一帧曝光时长。");
        if (Requested) return true;
        if (!isDue(exposureSeconds)) return false;
        if (Flipped) throw new InvalidOperationException("同一目标再次请求翻转；不自动重复运动，请检查镜筒侧和翻转配置。");
        return Requested = true;
    }

    public void ConfirmFlip()
    {
        if (!Requested || Flipped) throw new InvalidOperationException("没有待确认的唯一翻转请求。");
        Requested = false; Flipped = true;
        segmentSide = AfterSide;
    }

}
