using System.Security.Cryptography;
using System.Text.Json;
using System.IO;
using UvexAdv.Phd2;

namespace UvexAdv.Nina.Plugin;

internal enum Phd2LockShiftPendingPhase
{
    StageIntent = 0,
    AwaitingOperationBoundSettle = 1,
    AwaitingFreshResidual = 2,
    ReturnRequired = 3,
    SettledBudgetLedger = 4,
}

/// <summary>
/// Durable, run/config/topology/guide-epoch-bound ledger for PHD2 runtime lock
/// changes.  The origin is read from PHD2 immediately before this lineage; it
/// is not a stored optical offset and is never written to the PHD2 profile.
/// </summary>
internal sealed record Phd2LockShiftPendingState(
    int SchemaVersion,
    string ObservationRunId,
    string LineageId,
    string ActionConfigurationSha256,
    string CommissioningPresetSha256,
    string RecoveryContextSha256,
    string CalibrationQualityPolicyId,
    string CalibrationQualityPolicySha256,
    string TopologyFingerprintSha256,
    Phd2SlitGuideMode GuideMode,
    long ConnectionEpoch,
    long GuideEpoch,
    double OriginLockX,
    double OriginLockY,
    double CurrentLockX,
    double CurrentLockY,
    double RequestedLockX,
    double RequestedLockY,
    double MaximumStagePixels,
    double MaximumCumulativePixels,
    int MaximumAttempts,
    double MaximumElapsedSeconds,
    double CumulativeCommandedPixels,
    int AttemptsUsed,
    DateTimeOffset StartedUtc,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    Phd2LockShiftPendingPhase Phase,
    string? LastAcceptedFrameSha256,
    string? LastFramePath,
    string? IntentEvidencePath,
    string? LastReason,
    double OriginTargetX = double.NaN,
    double OriginTargetY = double.NaN,
    double OriginSlitX = double.NaN,
    double OriginSlitY = double.NaN)
{
    public const int CurrentSchemaVersion = 3;

    /// <summary>
    /// Advances only the process-local guide epoch after a locally issued lock
    /// mutation or guide/settle operation has produced fresh readback proof.
    /// Durable motion debt, attempt/pixel budgets, lineage and start time are
    /// deliberately preserved.
    /// </summary>
    public Phd2LockShiftPendingState RebindAfterLocallyAttestedGuideEpoch(
        long connectionEpoch,
        long guideEpoch,
        Phd2Point verifiedLock,
        DateTimeOffset nowUtc,
        string reason)
    {
        ArgumentNullException.ThrowIfNull(verifiedLock);
        if (connectionEpoch != ConnectionEpoch)
            throw new InvalidOperationException("A durable PHD2 lock lineage cannot be rebound across connection epochs.");
        if (guideEpoch < GuideEpoch)
            throw new InvalidOperationException("A durable PHD2 lock lineage cannot move backward to an older guide epoch.");
        if (!double.IsFinite(verifiedLock.X) || !double.IsFinite(verifiedLock.Y) ||
            verifiedLock.X < 0 || verifiedLock.Y < 0)
            throw new ArgumentOutOfRangeException(nameof(verifiedLock));
        if (nowUtc < UpdatedUtc)
            throw new ArgumentOutOfRangeException(nameof(nowUtc));
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("A guide-epoch rebind reason is required.", nameof(reason));

        return this with
        {
            GuideEpoch = guideEpoch,
            CurrentLockX = verifiedLock.X,
            CurrentLockY = verifiedLock.Y,
            UpdatedUtc = nowUtc,
            LastReason = reason,
        };
    }

    public Phd2LockShiftLedger ToPlannerLedger() => new(
        LineageId,
        new Phd2Point(OriginLockX, OriginLockY),
        new Phd2Point(CurrentLockX, CurrentLockY),
        AttemptsUsed,
        CumulativeCommandedPixels,
        StartedUtc,
        LastAcceptedFrameSha256);

    public IReadOnlyList<string> Validate()
    {
        var issues = new List<string>();
        if (SchemaVersion != CurrentSchemaVersion) issues.Add($"PHD2 lock-shift pending schema must be {CurrentSchemaVersion}.");
        if (string.IsNullOrWhiteSpace(ObservationRunId)) issues.Add("Observation run id is missing.");
        if (!Guid.TryParseExact(LineageId, "N", out _)) issues.Add("Lineage id must be a GUID in N format.");
        if (!IsSha(ActionConfigurationSha256)) issues.Add("Action-configuration SHA-256 is invalid.");
        if (!IsSha(CommissioningPresetSha256)) issues.Add("Commissioning-preset SHA-256 is invalid.");
        if (!IsSha(RecoveryContextSha256)) issues.Add("Recovery-context SHA-256 is invalid.");
        if (string.IsNullOrWhiteSpace(CalibrationQualityPolicyId)) issues.Add("Calibration-quality policy id is missing.");
        if (!IsSha(CalibrationQualityPolicySha256)) issues.Add("Calibration-quality policy SHA-256 is invalid.");
        if (!IsSha(TopologyFingerprintSha256)) issues.Add("Topology fingerprint SHA-256 is invalid.");
        if (!Enum.IsDefined(GuideMode) || !Enum.IsDefined(Phase)) issues.Add("Guide mode or pending phase is invalid.");
        if (ConnectionEpoch <= 0 || GuideEpoch <= 0) issues.Add("Connection and guide epochs must be positive.");
        ValidatePoint(issues, OriginLockX, OriginLockY, "origin lock");
        ValidatePoint(issues, OriginTargetX, OriginTargetY, "origin target");
        ValidatePoint(issues, OriginSlitX, OriginSlitY, "origin slit");
        ValidatePoint(issues, CurrentLockX, CurrentLockY, "current lock");
        ValidatePoint(issues, RequestedLockX, RequestedLockY, "requested lock");
        if (!double.IsFinite(MaximumStagePixels) || MaximumStagePixels <= 0) issues.Add("Maximum stage pixels is invalid.");
        if (!double.IsFinite(MaximumCumulativePixels) || MaximumCumulativePixels < MaximumStagePixels) issues.Add("Maximum cumulative pixels is invalid.");
        if (MaximumAttempts <= 0) issues.Add("Maximum attempts is invalid.");
        if (!double.IsFinite(MaximumElapsedSeconds) || MaximumElapsedSeconds <= 0) issues.Add("Maximum elapsed seconds is invalid.");
        if (!double.IsFinite(CumulativeCommandedPixels) || CumulativeCommandedPixels < 0 || CumulativeCommandedPixels > MaximumCumulativePixels + 1e-9)
            issues.Add("Consumed cumulative pixels is invalid.");
        if (AttemptsUsed < 0 || AttemptsUsed > MaximumAttempts) issues.Add("Consumed attempts is invalid.");
        if (StartedUtc == default || CreatedUtc < StartedUtc || UpdatedUtc < CreatedUtc) issues.Add("Pending timestamps are invalid.");
        if (LastAcceptedFrameSha256 is not null && !IsSha(LastAcceptedFrameSha256)) issues.Add("Last accepted frame SHA-256 is invalid.");
        return issues.AsReadOnly();
    }

    private static void ValidatePoint(ICollection<string> issues, double x, double y, string label)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y)) issues.Add($"{label} is non-finite.");
    }

    private static bool IsSha(string? value) => value?.Length == 64 && value.All(Uri.IsHexDigit);
}

internal sealed record Phd2LockShiftPendingLoadResult(Phd2LockShiftPendingState? State, string? Error);
internal sealed record Phd2LockShiftPendingFileResult(string Path, Phd2LockShiftPendingState? State, string? Error);

internal static class Phd2LockShiftBudgetHandoff
{
    private const double Epsilon = 1e-9;

    public static Phd2LockShiftPendingState CreateCurrentRunSettledCopy(
        Phd2LockShiftPendingState settledSource,
        string currentObservationRunId,
        string currentRecoveryContextSha256,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(settledSource);
        var issues = settledSource.Validate();
        if (issues.Count > 0) throw new InvalidOperationException(string.Join(" ", issues));
        if (settledSource.Phase != Phd2LockShiftPendingPhase.SettledBudgetLedger ||
            Distance(settledSource.CurrentLockX, settledSource.CurrentLockY, settledSource.OriginLockX, settledSource.OriginLockY) > Epsilon ||
            Distance(settledSource.RequestedLockX, settledSource.RequestedLockY, settledSource.OriginLockX, settledSource.OriginLockY) > Epsilon)
        {
            throw new InvalidOperationException("A current-run PHD2 budget handoff can only be created after a freshly verified return has settled at the durable origin.");
        }
        if (string.IsNullOrWhiteSpace(currentObservationRunId))
            throw new ArgumentException("Current observation run id is required.", nameof(currentObservationRunId));
        if (!IsSha(currentRecoveryContextSha256))
            throw new ArgumentException("Current recovery-context SHA-256 is invalid.", nameof(currentRecoveryContextSha256));
        if (nowUtc < settledSource.StartedUtc)
            throw new ArgumentOutOfRangeException(nameof(nowUtc), "Handoff time cannot precede the inherited budget clock.");

        return settledSource with
        {
            ObservationRunId = currentObservationRunId,
            RecoveryContextSha256 = currentRecoveryContextSha256,
            CreatedUtc = nowUtc,
            UpdatedUtc = nowUtc,
            IntentEvidencePath = null,
            LastReason = $"Settled PHD2 budget lineage handed off from run {settledSource.ObservationRunId}; lineage, limits, consumed attempts/pixels and earliest clock were preserved.",
        };
    }

    public static IReadOnlyList<string> ValidateCompletedHandoff(
        Phd2LockShiftPendingState source,
        Phd2LockShiftPendingState currentCopy,
        string expectedCurrentRunId,
        string expectedCurrentRecoveryContextSha256)
    {
        var issues = new List<string>();
        issues.AddRange(source.Validate().Select(issue => $"source: {issue}"));
        issues.AddRange(currentCopy.Validate().Select(issue => $"current copy: {issue}"));
        if (!string.Equals(currentCopy.ObservationRunId, expectedCurrentRunId, StringComparison.Ordinal))
            issues.Add("current copy run id differs from the explicit run");
        if (!SameHash(currentCopy.RecoveryContextSha256, expectedCurrentRecoveryContextSha256))
            issues.Add("current copy recovery context differs from the explicit run");
        if (currentCopy.Phase != Phd2LockShiftPendingPhase.SettledBudgetLedger)
            issues.Add("current copy is not a settled budget ledger");
        if (!string.Equals(source.LineageId, currentCopy.LineageId, StringComparison.Ordinal))
            issues.Add("lineage id changed");
        if (!SameHash(source.ActionConfigurationSha256, currentCopy.ActionConfigurationSha256) ||
            !SameHash(source.CommissioningPresetSha256, currentCopy.CommissioningPresetSha256) ||
            !string.Equals(source.CalibrationQualityPolicyId, currentCopy.CalibrationQualityPolicyId, StringComparison.Ordinal) ||
            !SameHash(source.CalibrationQualityPolicySha256, currentCopy.CalibrationQualityPolicySha256) ||
            !SameHash(source.TopologyFingerprintSha256, currentCopy.TopologyFingerprintSha256))
            issues.Add("action, preset, policy or topology binding changed");
        if (source.GuideMode != currentCopy.GuideMode ||
            !Same(source.MaximumStagePixels, currentCopy.MaximumStagePixels) ||
            !Same(source.MaximumCumulativePixels, currentCopy.MaximumCumulativePixels) ||
            source.MaximumAttempts != currentCopy.MaximumAttempts ||
            !Same(source.MaximumElapsedSeconds, currentCopy.MaximumElapsedSeconds))
            issues.Add("guide mode or a bounded-motion limit changed");
        if (source.AttemptsUsed != currentCopy.AttemptsUsed ||
            !Same(source.CumulativeCommandedPixels, currentCopy.CumulativeCommandedPixels) ||
            source.StartedUtc != currentCopy.StartedUtc)
            issues.Add("consumed attempts, pixels or earliest budget clock changed");
        if (!Same(source.OriginLockX, currentCopy.OriginLockX) ||
            !Same(source.OriginLockY, currentCopy.OriginLockY) ||
            !Same(source.CurrentLockX, source.OriginLockX) ||
            !Same(source.CurrentLockY, source.OriginLockY) ||
            !Same(source.RequestedLockX, source.OriginLockX) ||
            !Same(source.RequestedLockY, source.OriginLockY) ||
            !Same(currentCopy.CurrentLockX, currentCopy.OriginLockX) ||
            !Same(currentCopy.CurrentLockY, currentCopy.OriginLockY) ||
            !Same(currentCopy.RequestedLockX, currentCopy.OriginLockX) ||
            !Same(currentCopy.RequestedLockY, currentCopy.OriginLockY))
            issues.Add("current copy does not attest the same settled runtime-lock origin");
        return issues.AsReadOnly();
    }

    private static bool Same(double left, double right) =>
        double.IsFinite(left) && double.IsFinite(right) && Math.Abs(left - right) <= Epsilon;

    private static double Distance(double x1, double y1, double x2, double y2)
    {
        var dx = x1 - x2;
        var dy = y1 - y2;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static bool SameHash(string? left, string? right) =>
        string.Equals(NormalizeHash(left), NormalizeHash(right), StringComparison.OrdinalIgnoreCase);

    private static bool IsSha(string? value) => NormalizeHash(value).Length == 64 && NormalizeHash(value).All(Uri.IsHexDigit);

    private static string NormalizeHash(string? value) =>
        (value ?? string.Empty).Replace("-", string.Empty, StringComparison.Ordinal).Trim();
}

internal static class Phd2LockShiftPendingStore
{
    private sealed record Envelope(Phd2LockShiftPendingState State, string StateSha256);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static async Task WriteAtomicAsync(
        string path,
        Phd2LockShiftPendingState state,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(state);
        var issues = state.Validate();
        if (issues.Count > 0) throw new InvalidOperationException($"Invalid PHD2 lock-shift pending state: {string.Join(" ", issues)}");
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var stateBytes = JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions);
        var envelope = new Envelope(state, Convert.ToHexString(SHA256.HashData(stateBytes)));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
        var temporary = fullPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    public static async Task<Phd2LockShiftPendingLoadResult> LoadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath)) return new Phd2LockShiftPendingLoadResult(null, null);
            var bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
            var envelope = JsonSerializer.Deserialize<Envelope>(bytes, JsonOptions);
            if (envelope?.State is null || string.IsNullOrWhiteSpace(envelope.StateSha256))
                return new Phd2LockShiftPendingLoadResult(null, "PHD2 lock-shift pending envelope is empty.");
            var stateBytes = JsonSerializer.SerializeToUtf8Bytes(envelope.State, JsonOptions);
            var actual = Convert.ToHexString(SHA256.HashData(stateBytes));
            if (!string.Equals(actual, envelope.StateSha256, StringComparison.OrdinalIgnoreCase))
                return new Phd2LockShiftPendingLoadResult(null, "PHD2 lock-shift pending state SHA-256 mismatch.");
            var issues = envelope.State.Validate();
            return issues.Count == 0
                ? new Phd2LockShiftPendingLoadResult(envelope.State, null)
                : new Phd2LockShiftPendingLoadResult(null, string.Join(" ", issues));
        }
        catch (Exception ex)
        {
            return new Phd2LockShiftPendingLoadResult(null, ex.Message);
        }
    }

    public static async Task<IReadOnlyList<Phd2LockShiftPendingFileResult>> DiscoverAsync(
        string observationsRoot,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(observationsRoot);
        var root = Path.GetFullPath(observationsRoot);
        if (!Directory.Exists(root)) return Array.Empty<Phd2LockShiftPendingFileResult>();
        var results = new List<Phd2LockShiftPendingFileResult>();
        foreach (var path in Directory
                     .EnumerateFiles(root, "phd2-lock-shift-pending.json", SearchOption.AllDirectories)
                     .OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var loaded = await LoadAsync(path, cancellationToken).ConfigureAwait(false);
            results.Add(new Phd2LockShiftPendingFileResult(path, loaded.State, loaded.Error));
        }
        return results.AsReadOnly();
    }
}
