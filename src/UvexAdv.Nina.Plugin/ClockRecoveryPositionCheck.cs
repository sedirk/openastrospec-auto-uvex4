using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

// The shared/night hash excludes the segment's remaining science counts. The
// complete segment action hash deliberately changes when those counts change.
internal sealed record ClockRecoveryPositionIdentity(
    EquatorialTarget Target,
    string SharedActionConfigurationSha256,
    string CommissioningPresetSha256,
    string TelescopeId,
    Guid NinaProfileId,
    string Phd2Endpoint,
    int Phd2ProfileId,
    string Phd2ProfileName,
    string G3StableCameraId);

internal sealed record ClockRecoveryPositionSnapshot(
    ClockRecoveryPositionIdentity Identity,
    G3FrameMountReadback Mount,
    bool Connected,
    bool Stationary,
    bool Tracking,
    bool OwnersStoppedAndJoined,
    bool Parked = false);

// In-memory, one-use permission to inspect NEW evidence at the current pointing.
// This contains no old image, target pixel, slit, WCS, guide epoch or motion
// command. Its rejection only declines the optimization; the production adapter
// independently decides whether ordinary acquisition is safe to attempt.
internal sealed class ClockRecoveryPositionCheck
{
    private int attempted;

    private ClockRecoveryPositionCheck(string sourceRunId, ClockRecoveryPositionSnapshot before,
        ClockRecoveryPositionSnapshot after, DateTimeOffset issuedUtc,
        DateTimeOffset originalRecoveryDeadlineUtc, double maximumSeparationArcseconds)
    {
        SourceRunId = sourceRunId;
        Before = before;
        After = after;
        IssuedUtc = issuedUtc;
        OriginalRecoveryDeadlineUtc = originalRecoveryDeadlineUtc;
        MaximumSeparationArcseconds = maximumSeparationArcseconds;
    }

    internal string SourceRunId { get; }
    internal ClockRecoveryPositionSnapshot Before { get; }
    internal ClockRecoveryPositionSnapshot After { get; }
    internal DateTimeOffset IssuedUtc { get; }
    internal DateTimeOffset OriginalRecoveryDeadlineUtc { get; }
    internal double MaximumSeparationArcseconds { get; }
    internal bool Attempted => Volatile.Read(ref attempted) != 0;
    internal string? AttemptedRunId { get; private set; }
    internal string? ConsumedRunId { get; private set; }

    internal static ClockRecoveryPositionCheck? TryCreate(string sourceRunId, GateResult clockRepair,
        ClockRecoveryPositionSnapshot before, ClockRecoveryPositionSnapshot after,
        DateTimeOffset issuedUtc, DateTimeOffset originalRecoveryDeadlineUtc,
        double maximumSeparationArcseconds, out string reason)
    {
        reason = "Clock repair, stopped ownership or current pointing does not permit a current-field check.";
        if (string.IsNullOrWhiteSpace(sourceRunId) || clockRepair.Disposition != GateDisposition.Passed ||
            !Ready(before) || !Ready(after) || before.Identity != after.Identity ||
            issuedUtc == default || issuedUtc >= originalRecoveryDeadlineUtc ||
            before.Mount.ReportedUtc > after.Mount.ReportedUtc || after.Mount.ReportedUtc > issuedUtc ||
            !SamePointing(before.Mount, after.Mount, maximumSeparationArcseconds)) return null;
        reason = "Verified clock repair permits one same-target current-field check within the original deadline; no optical evidence is inherited.";
        return new(sourceRunId, before, after, issuedUtc, originalRecoveryDeadlineUtc, maximumSeparationArcseconds);
    }

    internal bool TryConsume(string continuationRunId, ClockRecoveryPositionSnapshot current,
        DateTimeOffset now, out string reason)
    {
        reason = "The current-field receipt has already been attempted; use the ordinary acquisition route if its own gates permit it.";
        if (Interlocked.CompareExchange(ref attempted, 1, 0) != 0) return false;
        AttemptedRunId = continuationRunId;
        reason = "The one-use current-field receipt no longer matches this segment, owner, deadline or pointing; no old optical state is authorized.";
        if (string.IsNullOrWhiteSpace(continuationRunId) || continuationRunId == SourceRunId ||
            now < IssuedUtc || now >= OriginalRecoveryDeadlineUtc || !Ready(current) ||
            current.Identity != After.Identity || current.Mount.ReportedUtc < IssuedUtc ||
            current.Mount.ReportedUtc > now ||
            !SamePointing(After.Mount, current.Mount, MaximumSeparationArcseconds)) return false;
        ConsumedRunId = continuationRunId;
        reason = "One same-target current-field check consumed; only newly captured and independently validated G3/slit evidence may advance acquisition.";
        return true;
    }

    private static bool Ready(ClockRecoveryPositionSnapshot snapshot) => snapshot.Connected &&
        snapshot.Stationary && snapshot.Tracking && snapshot.OwnersStoppedAndJoined && !snapshot.Parked &&
        ValidIdentity(snapshot.Identity) && ValidMount(snapshot.Mount);

    private static bool ValidIdentity(ClockRecoveryPositionIdentity identity) =>
        !string.IsNullOrWhiteSpace(identity.Target.Name) && !string.IsNullOrWhiteSpace(identity.Target.CatalogId) &&
        ValidCoordinates(identity.Target.RightAscensionDegrees, identity.Target.DeclinationDegrees) &&
        ValidHash(identity.SharedActionConfigurationSha256) && ValidHash(identity.CommissioningPresetSha256) &&
        !string.IsNullOrWhiteSpace(identity.TelescopeId) && identity.NinaProfileId != Guid.Empty &&
        !string.IsNullOrWhiteSpace(identity.Phd2Endpoint) && identity.Phd2ProfileId >= 0 &&
        !string.IsNullOrWhiteSpace(identity.Phd2ProfileName) && !string.IsNullOrWhiteSpace(identity.G3StableCameraId);

    private static bool ValidHash(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

    private static bool ValidMount(G3FrameMountReadback readback) =>
        ValidCoordinates(readback.RightAscensionDegrees, readback.DeclinationDegrees) &&
        !string.IsNullOrWhiteSpace(readback.CoordinateEpoch) && readback.ReportedUtc != default &&
        (string.Equals(readback.PierSide, "pierEast", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(readback.PierSide, "pierWest", StringComparison.OrdinalIgnoreCase));

    private static bool ValidCoordinates(double ra, double dec) =>
        double.IsFinite(ra) && ra is >= 0 and < 360 && double.IsFinite(dec) && dec is >= -90 and <= 90;

    private static bool SamePointing(G3FrameMountReadback a, G3FrameMountReadback b, double limit) =>
        double.IsFinite(limit) && limit > 0 &&
        string.Equals(a.CoordinateEpoch, b.CoordinateEpoch, StringComparison.Ordinal) &&
        string.Equals(a.PierSide, b.PierSide, StringComparison.OrdinalIgnoreCase) &&
        G3AcquisitionMotionPlanner.AngularSeparationArcseconds(a.RightAscensionDegrees, a.DeclinationDegrees,
            b.RightAscensionDegrees, b.DeclinationDegrees) <= limit + 1e-9;
}
