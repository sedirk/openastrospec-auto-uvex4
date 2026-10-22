using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

// Only the existing, bound native owner may produce this read-only snapshot.
// Explicit schema/freshness and positive booleans make old or partial responses
// fail closed; this does not replace transport/session or device identity checks.
internal sealed record PhotometryOwnerIdleProof(int Version, DateTimeOffset TimestampUtc,
    bool CoordinatorIdle, bool CameraIdle, bool WheelIdle, bool FocuserIdle, bool PersistenceIdle)
{
    internal static GateResult Evaluate(PhotometryOwnerIdleProof? proof, DateTimeOffset now)
    {
        if (proof is null || proof.Version != 1 || proof.TimestampUtc == default ||
            proof.TimestampUtc > now.AddSeconds(1) || now - proof.TimestampUtc > TimeSpan.FromSeconds(5))
            return GateResult.Unknown("QHY_OWNER_IDLE_UNAVAILABLE", "Native QHY owner idle proof is missing, unsupported or stale.");
        var metrics = new Dictionary<string, double>
        {
            ["coordinatorIdle"] = proof.CoordinatorIdle ? 1 : 0,
            ["cameraIdle"] = proof.CameraIdle ? 1 : 0,
            ["wheelIdle"] = proof.WheelIdle ? 1 : 0,
            ["focuserIdle"] = proof.FocuserIdle ? 1 : 0,
            ["persistenceIdle"] = proof.PersistenceIdle ? 1 : 0,
        };
        return proof.CoordinatorIdle && proof.CameraIdle && proof.WheelIdle && proof.FocuserIdle && proof.PersistenceIdle
            ? GateResult.Pass("QHY_OWNER_IDLE_CONFIRMED", "Bound native QHY owner confirms jobs, native camera, wheel, focuser and pending persistence idle.", metrics)
            : GateResult.Unknown("QHY_OWNER_STOP_UNCONFIRMED", "Native QHY owner still reports busy, disconnected or unresolved state; no takeover was attempted.", metrics);
    }
}
