using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

// Bounded optical acquisition may fail without corrupting a device session.
// Preserve the producer's result through placement cleanup. Unknown exceptions
// and owner/output/epoch failures deliberately do not use this exception.
internal sealed class TargetQualityEvidenceException(string code, string message)
    : PhysicalActionGateException(GateResult.TargetQuality(code, message))
{
    internal StageResult AfterStop(bool stopConfirmed) => new(stopConfirmed
        ? Gate
        : GateResult.Unknown("PHD2_QUALITY_STOP_UNCONFIRMED",
            $"{Gate.Code}: {Gate.Message} Checked stop is unconfirmed; no next-target authority."));
}
