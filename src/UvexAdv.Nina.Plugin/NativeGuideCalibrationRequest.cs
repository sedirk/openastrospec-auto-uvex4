namespace UvexAdv.Nina.Plugin;

/// <summary>One explicit operator request; never an automatic quality retry.</summary>
internal sealed class NativeGuideCalibrationRequest
{
    internal bool Consumed { get; private set; }

    internal void RequireBoundary(bool requested, bool motionFree)
    {
        if (requested && !Consumed && !motionFree)
            throw new InvalidOperationException("PHD2_EXPLICIT_CALIBRATION_BOUNDARY_UNSAFE: 本目标要求强制校准，但当前存在运动恢复责任；本次不发校准命令，请先处理原始回程记录。");
    }

    internal bool Consume(bool requested, bool motionFree)
    {
        RequireBoundary(requested, motionFree);
        if (!requested || Consumed) return false;
        // Consume before dispatch: timeout, failed RPC, resume or reacquisition
        // must not authorize a second pulse-producing calibration operation.
        Consumed = true;
        return true;
    }
}
