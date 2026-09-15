using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

/// <summary>Read-only confirmation of a suspect cached owner sample, before any lifecycle action.</summary>
internal sealed class MountClockReadbackConfirmation
{
    internal const int MaximumSamples = 8;
    private int consecutiveValid;
    internal bool Observe(GateResult gate)
    {
        consecutiveValid = gate.Disposition == GateDisposition.Passed ? consecutiveValid + 1 : 0;
        return consecutiveValid >= 2;
    }

    internal static bool MayReconnect(ObservationStage stage, bool establishedGuideSession) =>
        stage == ObservationStage.ValidateNightSetup && !establishedGuideSession;
}
