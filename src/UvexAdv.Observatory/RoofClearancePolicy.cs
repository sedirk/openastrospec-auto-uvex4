namespace UvexAdv.Observatory;

/// <summary>Owner attestation, not a measurement inferred from Park or an image.</summary>
public sealed record RoofClearanceAttestation(
    int SchemaVersion,
    string EvidenceId,
    string ConfirmedBy,
    DateTimeOffset ConfirmedUtc,
    string TelescopeDeviceId,
    string RoofDeviceId,
    bool AllMountPositionsClear,
    string Basis);

public static class RoofClearancePolicy
{
    public static IReadOnlyList<string> Validate(bool independent, RoofClearanceAttestation? evidence,
        string telescopeId, string roofId, DateTimeOffset now)
    {
        if (!independent)
            return evidence is null ? [] : ["Roof clearance mode does not match the commissioned attestation."];
        if (evidence is null) return ["Non-interfering roof mode requires an explicit owner clearance attestation."];
        var errors = new List<string>();
        if (evidence.SchemaVersion != 1 || !evidence.AllMountPositionsClear ||
            string.IsNullOrWhiteSpace(evidence.EvidenceId) || string.IsNullOrWhiteSpace(evidence.ConfirmedBy) ||
            string.IsNullOrWhiteSpace(evidence.Basis) || evidence.ConfirmedUtc == default || evidence.ConfirmedUtc > now)
            errors.Add("Roof clearance attestation is incomplete, future-dated or does not cover all mount positions.");
        if (string.IsNullOrWhiteSpace(telescopeId) || string.IsNullOrWhiteSpace(roofId) ||
            !string.Equals(evidence.TelescopeDeviceId, telescopeId, StringComparison.Ordinal) ||
            !string.Equals(evidence.RoofDeviceId, roofId, StringComparison.Ordinal))
            errors.Add("Roof clearance attestation belongs to different mount/roof selections.");
        return errors;
    }

    public static bool IsStopped(bool connected, bool identityMatches, bool slewing, bool pulseGuiding, bool tracking) =>
        connected && identityMatches && !slewing && !pulseGuiding && !tracking;

    /// <summary>Only the attested non-interfering route: stopping and closure must finish before Park.</summary>
    public static async Task<string?> CloseThenParkAsync(Func<CancellationToken, Task<string?>> stop,
        Func<CancellationToken, Task<string?>> close, Func<CancellationToken, Task<string?>> park,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var issue = await stop(token).ConfigureAwait(false);
        if (issue is not null) return issue;
        token.ThrowIfCancellationRequested();
        issue = await close(token).ConfigureAwait(false);
        if (issue is not null) return issue;
        token.ThrowIfCancellationRequested();
        // A parking failure never prevents an already verified roof closure and never becomes success.
        try { return await park(token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) { return $"Roof closed; independent mount park failed: {ex.Message}"; }
    }
}
