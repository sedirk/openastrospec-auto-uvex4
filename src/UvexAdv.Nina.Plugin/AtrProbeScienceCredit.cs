using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

/// <summary>Final selected probes retain their immutable PROBE/SNAPSHOT source identity.</summary>
internal sealed class AtrProbeScienceCredit
{
    private readonly HashSet<string> credited = new(StringComparer.Ordinal);
    public bool TryCredit(string captureId, string sourceRunId, string currentRunId,
        double probeExposure, double selectedExposure, GateResult savedTemperature, GateResult scienceQuality,
        bool provenanceVerified, int accepted, int requested, int attempted, int maximumAttempts)
    {
        if (string.IsNullOrWhiteSpace(captureId) || string.IsNullOrWhiteSpace(currentRunId) || sourceRunId != currentRunId ||
            !double.IsFinite(probeExposure) || probeExposure <= 0 || !double.IsFinite(selectedExposure) ||
            Math.Abs(probeExposure - selectedExposure) > 1e-9 || !provenanceVerified ||
            savedTemperature.Disposition != GateDisposition.Passed || scienceQuality.Disposition != GateDisposition.Passed ||
            accepted < 0 || accepted >= requested || attempted < 0 || attempted >= maximumAttempts) return false;
        return credited.Add(captureId);
    }
}
