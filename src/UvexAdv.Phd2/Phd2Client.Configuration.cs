namespace UvexAdv.Phd2;

public sealed record Phd2ConfigurationChangeEvidence(
    long EventSequence, DateTimeOffset ReceivedUtc, string? BeforeSha256,
    string? AfterSha256, bool MaterialConfigurationUnchanged);

public sealed partial class Phd2Client
{
    private string? configurationFingerprint;

    private string? ReadStableConfigurationFingerprint()
    {
        try
        {
            // Two complete identical reads; missing/unreadable/changing evidence
            // takes the existing conservative invalidation path.
            var first = options.ReadConfigurationFingerprint?.Invoke();
            var second = options.ReadConfigurationFingerprint?.Invoke();
            return first is { Length: 64 } && first.All(Uri.IsHexDigit) && first == second ? first : null;
        }
        catch (Exception) { return null; }
    }

    private Phd2StateSnapshot ApplyConfigurationChange(Phd2StateSnapshot current, Phd2EventMessage message)
    {
        var before = configurationFingerprint;
        var after = ReadStableConfigurationFingerprint();
        configurationFingerprint = after;
        var unchanged = before is not null && before == after;
        var next = current with
        {
            LastConfigurationChange = new(message.Sequence, message.ReceivedUtc, before, after, unchanged),
        };
        // PHD2 broadcasts the same event for screen Gamma and guiding settings.
        // Identical complete material evidence is NOT a new guiding operation.
        // Separate LostLock/stop/selection/flip events still invalidate normally.
        if (unchanged) return next;
        approvedIdentityValidation = null;
        next = next with { CalibrationValidation = null };
        return IsPendingSettleOperationCurrent(next) ? next : InvalidateSettle(next);
    }
}
