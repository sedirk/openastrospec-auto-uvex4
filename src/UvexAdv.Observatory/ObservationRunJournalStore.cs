using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UvexAdv.Observatory;

public sealed record ObservationRunLockedMetadata(
    string? NightSetupSha256 = null,
    string? CommissioningPresetSha256 = null,
    string? Phd2ProfileEvidenceSha256 = null,
    string? QhyConfigurationSha256 = null,
    IReadOnlyDictionary<string, string>? AdditionalHashes = null,
    IReadOnlyDictionary<string, string>? Labels = null)
{
    public static ObservationRunLockedMetadata Empty { get; } = new();
}

public sealed record ObservationRunCounters(
    long AtrAttemptedFrames = 0,
    long AtrAcceptedFrames = 0,
    long QhyAttemptedFrames = 0,
    long QhyAcceptedFrames = 0,
    IReadOnlyDictionary<string, long>? Additional = null)
{
    public static ObservationRunCounters Empty { get; } = new();
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ObservationRunJournalEntryKind
{
    Initialized = 0,
    Snapshot = 1,
    Gate = 2,
    Evidence = 3,
    Counters = 4,
}

public sealed record ObservationRunJournalEntry(
    long Revision,
    DateTimeOffset TimestampUtc,
    ObservationRunJournalEntryKind Kind,
    ObservationRunState State,
    ObservationStage? Stage,
    string Code,
    string Message,
    ObservationRunCounters Counters,
    string? EvidenceKind = null,
    string? EvidencePath = null,
    string? EvidenceSha256 = null,
    string? PauseReason = null);

/// <summary>
/// Durable, self-contained state for one observation run. Journal entries and
/// the latest materialized view are committed in the same atomic file replace,
/// so a reader never has to reconcile two independently written files.
/// </summary>
public sealed record ObservationRunManifest(
    int SchemaVersion,
    string ObservationRunId,
    long Revision,
    string PlanSha256,
    ObservationPlan Plan,
    ObservationSnapshot Snapshot,
    ObservationRunLockedMetadata LockedMetadata,
    string LockedMetadataSha256,
    IReadOnlyDictionary<ObservationStage, GateResult> Gates,
    IReadOnlyList<EvidenceReference> Evidence,
    ObservationRunCounters Counters,
    ObservationRunState? TerminalState,
    string? TerminalReason,
    string? PauseReason,
    IReadOnlyList<ObservationRunJournalEntry> Journal,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);

public sealed class ObservationRunRevisionConflictException : InvalidOperationException
{
    public ObservationRunRevisionConflictException(long expectedRevision, long actualRevision)
        : base($"Observation run manifest revision conflict: expected {expectedRevision}, actual {actualRevision}.")
    {
        ExpectedRevision = expectedRevision;
        ActualRevision = actualRevision;
    }

    public long ExpectedRevision { get; }

    public long ActualRevision { get; }
}

public sealed class ObservationRunManifestCorruptException : IOException
{
    public ObservationRunManifestCorruptException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Serializes all mutations for a manifest path, checks the revision currently
/// on disk, and replaces the complete manifest atomically. Separate store
/// instances in this process share a path gate; an exclusive lock file also
/// prevents two processes from committing the same base revision concurrently.
/// </summary>
public sealed class ObservationRunJournalStore
{
    public const int CurrentSchemaVersion = 1;

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> PathGates =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly SemaphoreSlim pathGate;

    public ObservationRunJournalStore(string manifestPath)
    {
        if (string.IsNullOrWhiteSpace(manifestPath))
        {
            throw new ArgumentException("Observation run manifest path is required.", nameof(manifestPath));
        }

        ManifestPath = Path.GetFullPath(manifestPath);
        LockPath = ManifestPath + ".lock";
        pathGate = PathGates.GetOrAdd(ManifestPath, static _ => new SemaphoreSlim(1, 1));
    }

    public string ManifestPath { get; }

    public string LockPath { get; }

    public async Task<ObservationRunManifest> InitializeAsync(
        ObservationPlan plan,
        ObservationRunLockedMetadata? lockedMetadata = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var planIssues = plan.Validate();
        if (planIssues.Count > 0)
        {
            throw new ArgumentException($"Observation plan is invalid: {string.Join(" ", planIssues)}", nameof(plan));
        }

        var normalizedMetadata = NormalizeLockedMetadata(lockedMetadata ?? ObservationRunLockedMetadata.Empty);
        ValidateLockedMetadata(normalizedMetadata);
        var planSha256 = ComputeSha256(plan);
        var metadataSha256 = ComputeSha256(normalizedMetadata);

        return await WithExclusivePathAsync(async token =>
        {
            var existing = await ReadUnlockedAsync(token).ConfigureAwait(false);
            if (existing is not null)
            {
                if (!string.Equals(existing.ObservationRunId, plan.ObservationRunId, StringComparison.Ordinal) ||
                    !SameHash(existing.PlanSha256, planSha256))
                {
                    throw new InvalidOperationException(
                        $"Manifest '{ManifestPath}' already belongs to a different observation plan or run.");
                }

                if (!SameHash(existing.LockedMetadataSha256, metadataSha256))
                {
                    throw new InvalidOperationException(
                        "The existing run manifest is bound to different locked hash metadata.");
                }

                return existing;
            }

            var now = DateTimeOffset.UtcNow;
            var snapshot = new ObservationSnapshot(
                plan.ObservationRunId,
                ObservationRunState.Idle,
                null,
                ObservationRunCoordinator.Stages.FirstOrDefault(),
                "Observation run manifest initialized.",
                null,
                0,
                ObservationRunCoordinator.Stages.Count,
                now,
                Array.Empty<ObservationEvent>());
            var counters = ObservationRunCounters.Empty;
            var entry = new ObservationRunJournalEntry(
                1,
                now,
                ObservationRunJournalEntryKind.Initialized,
                snapshot.State,
                null,
                "RUN_MANIFEST_INITIALIZED",
                "Observation run manifest initialized and locked to its plan and hash metadata.",
                counters);
            var created = new ObservationRunManifest(
                CurrentSchemaVersion,
                plan.ObservationRunId,
                1,
                planSha256,
                plan,
                snapshot,
                normalizedMetadata,
                metadataSha256,
                new Dictionary<ObservationStage, GateResult>(),
                Array.Empty<EvidenceReference>(),
                counters,
                null,
                null,
                null,
                new[] { entry },
                now,
                now);
            ValidateManifest(created);
            await WriteAtomicUnlockedAsync(created, token).ConfigureAwait(false);
            return created;
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task<ObservationRunManifest> PublishSnapshotAsync(
        ObservationSnapshot snapshot,
        CancellationToken cancellationToken = default) =>
        PublishSnapshotCoreAsync(snapshot, counters: null, expectedRevision: null, cancellationToken);

    public Task<ObservationRunManifest> PublishSnapshotAsync(
        ObservationSnapshot snapshot,
        long expectedRevision,
        CancellationToken cancellationToken = default) =>
        PublishSnapshotCoreAsync(snapshot, counters: null, expectedRevision, cancellationToken);

    public Task<ObservationRunManifest> PublishSnapshotAsync(
        ObservationSnapshot snapshot,
        ObservationRunCounters counters,
        CancellationToken cancellationToken = default) =>
        PublishSnapshotCoreAsync(snapshot, counters, expectedRevision: null, cancellationToken);

    public Task<ObservationRunManifest> PublishSnapshotAsync(
        ObservationSnapshot snapshot,
        ObservationRunCounters counters,
        long expectedRevision,
        CancellationToken cancellationToken = default) =>
        PublishSnapshotCoreAsync(snapshot, counters, expectedRevision, cancellationToken);

    public Task<ObservationRunManifest> PublishGateAsync(
        ObservationStage stage,
        GateResult gate,
        CancellationToken cancellationToken = default) =>
        PublishGateCoreAsync(stage, gate, expectedRevision: null, cancellationToken);

    public Task<ObservationRunManifest> PublishGateAsync(
        ObservationStage stage,
        GateResult gate,
        long expectedRevision,
        CancellationToken cancellationToken = default) =>
        PublishGateCoreAsync(stage, gate, expectedRevision, cancellationToken);

    public Task<ObservationRunManifest> PublishEvidenceAsync(
        EvidenceReference evidence,
        CancellationToken cancellationToken = default) =>
        PublishEvidenceCoreAsync(evidence, expectedRevision: null, cancellationToken);

    public Task<ObservationRunManifest> PublishEvidenceAsync(
        EvidenceReference evidence,
        long expectedRevision,
        CancellationToken cancellationToken = default) =>
        PublishEvidenceCoreAsync(evidence, expectedRevision, cancellationToken);

    public Task<ObservationRunManifest> PublishCountersAsync(
        ObservationRunCounters counters,
        CancellationToken cancellationToken = default) =>
        PublishCountersCoreAsync(counters, expectedRevision: null, cancellationToken);

    public Task<ObservationRunManifest> PublishCountersAsync(
        ObservationRunCounters counters,
        long expectedRevision,
        CancellationToken cancellationToken = default) =>
        PublishCountersCoreAsync(counters, expectedRevision, cancellationToken);

    /// <summary>
    /// Commits the terminal snapshot together with the authoritative final gate
    /// view and counters in one atomic manifest replacement. Earlier evidence
    /// records are already part of the materialized manifest, so successful
    /// return is the durable completion acknowledgement for the whole run.
    /// </summary>
    public Task<ObservationRunManifest> CommitCompletionAsync(
        ObservationSnapshot completedSnapshot,
        ObservationRunCounters counters,
        IReadOnlyDictionary<ObservationStage, GateResult> gates,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(completedSnapshot);
        ArgumentNullException.ThrowIfNull(counters);
        ArgumentNullException.ThrowIfNull(gates);
        if (completedSnapshot.State != ObservationRunState.Completed)
        {
            throw new ArgumentException(
                "The durable completion commit requires a Completed snapshot.",
                nameof(completedSnapshot));
        }

        var normalizedCounters = NormalizeCounters(counters);
        var finalGates = new Dictionary<ObservationStage, GateResult>();
        foreach (var pair in gates)
        {
            ValidateGate(pair.Key, pair.Value);
            finalGates[pair.Key] = pair.Value with
            {
                Metrics = pair.Value.Metrics is null
                    ? null
                    : new SortedDictionary<string, double>(
                        pair.Value.Metrics.ToDictionary(
                            metric => metric.Key,
                            metric => metric.Value,
                            StringComparer.Ordinal),
                        StringComparer.Ordinal),
            };
        }

        return MutateAsync(current =>
        {
            if (!string.Equals(
                    completedSnapshot.ObservationRunId,
                    current.ObservationRunId,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Snapshot run id '{completedSnapshot.ObservationRunId}' does not match manifest run '{current.ObservationRunId}'.");
            }
            if (completedSnapshot.UpdatedUtc < current.Snapshot.UpdatedUtc)
            {
                throw new InvalidOperationException(
                    $"Completion timestamp {completedSnapshot.UpdatedUtc:O} is older than persisted snapshot {current.Snapshot.UpdatedUtc:O}.");
            }
            if (current.TerminalState is not null && current.TerminalState != ObservationRunState.Completed)
            {
                throw new InvalidOperationException(
                    $"A terminal {current.TerminalState} run cannot be committed as Completed.");
            }

            ValidateCounterProgression(current.Counters, normalizedCounters);
            var mergedGates = new Dictionary<ObservationStage, GateResult>(current.Gates);
            foreach (var pair in finalGates) mergedGates[pair.Key] = pair.Value;

            var revision = checked(current.Revision + 1);
            var latestEvent = completedSnapshot.RecentEvents.LastOrDefault();
            var entry = new ObservationRunJournalEntry(
                revision,
                completedSnapshot.UpdatedUtc,
                ObservationRunJournalEntryKind.Snapshot,
                ObservationRunState.Completed,
                completedSnapshot.CurrentStage,
                latestEvent?.Code ?? "RUN_COMPLETED",
                completedSnapshot.StatusMessage,
                normalizedCounters,
                PauseReason: completedSnapshot.PauseReason);
            return current with
            {
                Revision = revision,
                Snapshot = completedSnapshot,
                Gates = mergedGates,
                Counters = normalizedCounters,
                TerminalState = ObservationRunState.Completed,
                TerminalReason = completedSnapshot.StatusMessage,
                PauseReason = completedSnapshot.PauseReason,
                Journal = Append(current.Journal, entry),
                UpdatedUtc = Max(current.UpdatedUtc, completedSnapshot.UpdatedUtc),
            };
        }, expectedRevision: null, cancellationToken);
    }

    public Task<ObservationRunManifest?> ReadAsync(CancellationToken cancellationToken = default) =>
        WithExclusivePathAsync(ReadUnlockedAsync, cancellationToken);

    private Task<ObservationRunManifest> PublishSnapshotCoreAsync(
        ObservationSnapshot snapshot,
        ObservationRunCounters? counters,
        long? expectedRevision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return MutateAsync(current =>
        {
            if (!string.Equals(snapshot.ObservationRunId, current.ObservationRunId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Snapshot run id '{snapshot.ObservationRunId}' does not match manifest run '{current.ObservationRunId}'.");
            }

            if (snapshot.UpdatedUtc < current.Snapshot.UpdatedUtc)
            {
                throw new InvalidOperationException(
                    $"Snapshot timestamp {snapshot.UpdatedUtc:O} is older than persisted snapshot {current.Snapshot.UpdatedUtc:O}.");
            }

            if (current.TerminalState is not null && snapshot.State != current.TerminalState)
            {
                throw new InvalidOperationException(
                    $"A terminal {current.TerminalState} run cannot regress to {snapshot.State}.");
            }

            var nextCounters = counters is null ? current.Counters : NormalizeCounters(counters);
            ValidateCounterProgression(current.Counters, nextCounters);
            var terminal = IsTerminal(snapshot.State) ? snapshot.State : current.TerminalState;
            var terminalReason = IsTerminal(snapshot.State) ? snapshot.StatusMessage : current.TerminalReason;
            var latestEvent = snapshot.RecentEvents.LastOrDefault();
            var revision = checked(current.Revision + 1);
            var entry = new ObservationRunJournalEntry(
                revision,
                snapshot.UpdatedUtc,
                ObservationRunJournalEntryKind.Snapshot,
                snapshot.State,
                snapshot.CurrentStage,
                latestEvent?.Code ?? $"RUN_{snapshot.State.ToString().ToUpperInvariant()}",
                snapshot.StatusMessage,
                nextCounters,
                PauseReason: snapshot.PauseReason);
            return current with
            {
                Revision = revision,
                Snapshot = snapshot,
                Counters = nextCounters,
                TerminalState = terminal,
                TerminalReason = terminalReason,
                PauseReason = snapshot.PauseReason,
                Journal = Append(current.Journal, entry),
                UpdatedUtc = Max(current.UpdatedUtc, snapshot.UpdatedUtc),
            };
        }, expectedRevision, cancellationToken);
    }

    private Task<ObservationRunManifest> PublishGateCoreAsync(
        ObservationStage stage,
        GateResult gate,
        long? expectedRevision,
        CancellationToken cancellationToken)
    {
        ValidateGate(stage, gate);

        return MutateAsync(current =>
        {
            var now = DateTimeOffset.UtcNow;
            var revision = checked(current.Revision + 1);
            var gates = new Dictionary<ObservationStage, GateResult>(current.Gates)
            {
                [stage] = gate,
            };
            var entry = new ObservationRunJournalEntry(
                revision,
                now,
                ObservationRunJournalEntryKind.Gate,
                current.Snapshot.State,
                stage,
                gate.Code,
                gate.Message,
                current.Counters);
            return current with
            {
                Revision = revision,
                Gates = gates,
                Journal = Append(current.Journal, entry),
                UpdatedUtc = Max(current.UpdatedUtc, now),
            };
        }, expectedRevision, cancellationToken);
    }

    private Task<ObservationRunManifest> PublishEvidenceCoreAsync(
        EvidenceReference evidence,
        long? expectedRevision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ValidateEvidence(evidence);
        return MutateAsync(current =>
        {
            var samePath = current.Evidence.FirstOrDefault(item =>
                string.Equals(item.AbsolutePath, evidence.AbsolutePath, StringComparison.OrdinalIgnoreCase));
            if (samePath is not null)
            {
                if (!SameHash(samePath.Sha256, evidence.Sha256))
                {
                    throw new InvalidOperationException(
                        $"Immutable evidence path '{evidence.AbsolutePath}' was presented with a different SHA-256.");
                }

                return current;
            }

            var now = DateTimeOffset.UtcNow;
            var revision = checked(current.Revision + 1);
            var entry = new ObservationRunJournalEntry(
                revision,
                now,
                ObservationRunJournalEntryKind.Evidence,
                current.Snapshot.State,
                current.Snapshot.CurrentStage,
                "EVIDENCE_PUBLISHED",
                $"Evidence '{evidence.Kind}' was published with SHA-256 {NormalizeHash(evidence.Sha256)}.",
                current.Counters,
                evidence.Kind,
                evidence.AbsolutePath,
                NormalizeHash(evidence.Sha256));
            return current with
            {
                Revision = revision,
                Evidence = Append(current.Evidence, evidence with { Sha256 = NormalizeHash(evidence.Sha256) }),
                Journal = Append(current.Journal, entry),
                UpdatedUtc = Max(current.UpdatedUtc, now),
            };
        }, expectedRevision, cancellationToken);
    }

    private Task<ObservationRunManifest> PublishCountersCoreAsync(
        ObservationRunCounters counters,
        long? expectedRevision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(counters);
        var normalized = NormalizeCounters(counters);
        return MutateAsync(current =>
        {
            ValidateCounterProgression(current.Counters, normalized);
            if (current.Counters == normalized || CountersEqual(current.Counters, normalized)) return current;

            var now = DateTimeOffset.UtcNow;
            var revision = checked(current.Revision + 1);
            var entry = new ObservationRunJournalEntry(
                revision,
                now,
                ObservationRunJournalEntryKind.Counters,
                current.Snapshot.State,
                current.Snapshot.CurrentStage,
                "FRAME_COUNTERS_UPDATED",
                $"ATR accepted/attempted {normalized.AtrAcceptedFrames}/{normalized.AtrAttemptedFrames}; " +
                $"QHY accepted/attempted {normalized.QhyAcceptedFrames}/{normalized.QhyAttemptedFrames}.",
                normalized);
            return current with
            {
                Revision = revision,
                Counters = normalized,
                Journal = Append(current.Journal, entry),
                UpdatedUtc = Max(current.UpdatedUtc, now),
            };
        }, expectedRevision, cancellationToken);
    }

    private Task<ObservationRunManifest> MutateAsync(
        Func<ObservationRunManifest, ObservationRunManifest> mutation,
        long? expectedRevision,
        CancellationToken cancellationToken) =>
        WithExclusivePathAsync(async token =>
        {
            var current = await ReadUnlockedAsync(token).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"Observation run manifest '{ManifestPath}' has not been initialized.");
            if (expectedRevision is { } expected && current.Revision != expected)
            {
                throw new ObservationRunRevisionConflictException(expected, current.Revision);
            }

            var next = mutation(current);
            if (ReferenceEquals(next, current) || next == current) return current;
            if (next.Revision != checked(current.Revision + 1))
            {
                throw new InvalidOperationException(
                    $"Manifest mutation must advance revision exactly once from {current.Revision} to {current.Revision + 1}.");
            }

            EnsureImmutableBindings(current, next);
            ValidateManifest(next);
            await WriteAtomicUnlockedAsync(next, token).ConfigureAwait(false);
            return next;
        }, cancellationToken);

    private async Task<T> WithExclusivePathAsync<T>(
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        await pathGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(ManifestPath)
                ?? throw new InvalidOperationException("Manifest path has no parent directory.");
            Directory.CreateDirectory(directory);
            await using var processLock = await AcquireProcessLockAsync(cancellationToken).ConfigureAwait(false);
            return await action(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            pathGate.Release();
        }
    }

    private async Task<FileStream> AcquireProcessLockAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    LockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous | FileOptions.WriteThrough);
            }
            catch (IOException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<ObservationRunManifest?> ReadUnlockedAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(ManifestPath)) return null;
        try
        {
            await using var stream = new FileStream(
                ManifestPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var manifest = await JsonSerializer.DeserializeAsync<ObservationRunManifest>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            if (manifest is null)
            {
                throw new ObservationRunManifestCorruptException($"Manifest '{ManifestPath}' is empty.");
            }

            ValidateManifest(manifest);
            return manifest;
        }
        catch (ObservationRunManifestCorruptException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
        {
            throw new ObservationRunManifestCorruptException(
                $"Manifest '{ManifestPath}' could not be read or validated: {ex.Message}",
                ex);
        }
    }

    private async Task WriteAtomicUnlockedAsync(
        ObservationRunManifest manifest,
        CancellationToken cancellationToken)
    {
        var temporaryPath = ManifestPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, manifest, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(ManifestPath))
            {
                File.Replace(temporaryPath, ManifestPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, ManifestPath);
            }

            // The temp file was flushed before the atomic rename/replace. Flush
            // the committed path as well so successful return is an explicit
            // durability acknowledgement, not merely a page-cache acknowledgement.
            using var committed = new FileStream(
                ManifestPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.Read,
                1,
                FileOptions.WriteThrough);
            committed.Flush(flushToDisk: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static void EnsureImmutableBindings(
        ObservationRunManifest current,
        ObservationRunManifest next)
    {
        if (next.SchemaVersion != current.SchemaVersion ||
            !string.Equals(next.ObservationRunId, current.ObservationRunId, StringComparison.Ordinal) ||
            !SameHash(next.PlanSha256, current.PlanSha256) ||
            !SameHash(next.LockedMetadataSha256, current.LockedMetadataSha256) ||
            !SameHash(ComputeSha256(next.Plan), current.PlanSha256) ||
            !SameHash(ComputeSha256(NormalizeLockedMetadata(next.LockedMetadata)), current.LockedMetadataSha256))
        {
            throw new InvalidOperationException(
                "Observation run identity, plan, schema, or locked hash metadata cannot change after initialization.");
        }
    }

    private static void ValidateManifest(ObservationRunManifest manifest)
    {
        if (manifest.SchemaVersion != CurrentSchemaVersion)
        {
            throw new ObservationRunManifestCorruptException(
                $"Unsupported observation run manifest schema {manifest.SchemaVersion}.");
        }

        if (string.IsNullOrWhiteSpace(manifest.ObservationRunId) || manifest.Revision < 1)
        {
            throw new ObservationRunManifestCorruptException("Manifest run id or revision is invalid.");
        }

        if (!string.Equals(manifest.Plan.ObservationRunId, manifest.ObservationRunId, StringComparison.Ordinal) ||
            !SameHash(ComputeSha256(manifest.Plan), manifest.PlanSha256))
        {
            throw new ObservationRunManifestCorruptException("Manifest plan identity or SHA-256 is invalid.");
        }

        var normalizedMetadata = NormalizeLockedMetadata(manifest.LockedMetadata);
        ValidateLockedMetadata(normalizedMetadata);
        if (!SameHash(ComputeSha256(normalizedMetadata), manifest.LockedMetadataSha256))
        {
            throw new ObservationRunManifestCorruptException("Manifest locked metadata SHA-256 is invalid.");
        }

        ValidateCounters(manifest.Counters);
        if (manifest.Journal.Count == 0 || manifest.Journal[^1].Revision != manifest.Revision)
        {
            throw new ObservationRunManifestCorruptException(
                "Manifest journal is empty or does not end at the materialized revision.");
        }

        long previous = 0;
        foreach (var entry in manifest.Journal)
        {
            if (entry.Revision != previous + 1)
            {
                throw new ObservationRunManifestCorruptException("Manifest journal revisions are not contiguous.");
            }
            ValidateCounters(entry.Counters);
            previous = entry.Revision;
        }

        if (!string.Equals(manifest.Snapshot.ObservationRunId, manifest.ObservationRunId, StringComparison.Ordinal))
        {
            throw new ObservationRunManifestCorruptException("Manifest snapshot belongs to a different run.");
        }

        if (manifest.TerminalState is { } terminal && !IsTerminal(terminal))
        {
            throw new ObservationRunManifestCorruptException($"Manifest terminal state {terminal} is not terminal.");
        }

        foreach (var evidence in manifest.Evidence) ValidateEvidence(evidence);
    }

    private static ObservationRunLockedMetadata NormalizeLockedMetadata(ObservationRunLockedMetadata metadata) =>
        metadata with
        {
            NightSetupSha256 = NormalizeNullableHash(metadata.NightSetupSha256),
            CommissioningPresetSha256 = NormalizeNullableHash(metadata.CommissioningPresetSha256),
            Phd2ProfileEvidenceSha256 = NormalizeNullableHash(metadata.Phd2ProfileEvidenceSha256),
            QhyConfigurationSha256 = NormalizeNullableHash(metadata.QhyConfigurationSha256),
            AdditionalHashes = metadata.AdditionalHashes is null
                ? null
                : new SortedDictionary<string, string>(
                    metadata.AdditionalHashes.ToDictionary(
                        pair => pair.Key.Trim(),
                        pair => NormalizeHash(pair.Value),
                        StringComparer.Ordinal),
                    StringComparer.Ordinal),
            Labels = metadata.Labels is null
                ? null
                : new SortedDictionary<string, string>(
                    metadata.Labels.ToDictionary(
                        pair => pair.Key.Trim(),
                        pair => pair.Value.Trim(),
                        StringComparer.Ordinal),
                    StringComparer.Ordinal),
        };

    private static ObservationRunCounters NormalizeCounters(ObservationRunCounters counters) =>
        counters with
        {
            Additional = counters.Additional is null
                ? null
                : new SortedDictionary<string, long>(
                    counters.Additional.ToDictionary(
                        pair => pair.Key.Trim(),
                        pair => pair.Value,
                        StringComparer.Ordinal),
                    StringComparer.Ordinal),
        };

    private static void ValidateLockedMetadata(ObservationRunLockedMetadata metadata)
    {
        ValidateOptionalHash(metadata.NightSetupSha256, nameof(metadata.NightSetupSha256));
        ValidateOptionalHash(metadata.CommissioningPresetSha256, nameof(metadata.CommissioningPresetSha256));
        ValidateOptionalHash(metadata.Phd2ProfileEvidenceSha256, nameof(metadata.Phd2ProfileEvidenceSha256));
        ValidateOptionalHash(metadata.QhyConfigurationSha256, nameof(metadata.QhyConfigurationSha256));
        if (metadata.AdditionalHashes is not null)
        {
            foreach (var pair in metadata.AdditionalHashes)
            {
                if (string.IsNullOrWhiteSpace(pair.Key)) throw new ArgumentException("Locked hash names cannot be blank.", nameof(metadata));
                ValidateOptionalHash(pair.Value, pair.Key, required: true);
            }
        }
        if (metadata.Labels?.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value)) == true)
        {
            throw new ArgumentException("Locked metadata labels cannot contain blank keys or values.", nameof(metadata));
        }
    }

    private static void ValidateEvidence(EvidenceReference evidence)
    {
        if (string.IsNullOrWhiteSpace(evidence.Kind)) throw new ArgumentException("Evidence kind is required.", nameof(evidence));
        if (!Path.IsPathFullyQualified(evidence.AbsolutePath)) throw new ArgumentException("Evidence path must be absolute.", nameof(evidence));
        ValidateOptionalHash(evidence.Sha256, nameof(evidence.Sha256), required: true);
        if (evidence.TimestampUtc == default) throw new ArgumentException("Evidence timestamp is required.", nameof(evidence));
    }

    private static void ValidateGate(ObservationStage stage, GateResult gate)
    {
        ArgumentNullException.ThrowIfNull(gate);
        if (!Enum.IsDefined(stage)) throw new ArgumentOutOfRangeException(nameof(stage));
        if (string.IsNullOrWhiteSpace(gate.Code))
        {
            throw new ArgumentException("Gate code is required.", nameof(gate));
        }
    }

    private static void ValidateCounterProgression(
        ObservationRunCounters current,
        ObservationRunCounters next)
    {
        ValidateCounters(next);
        if (next.AtrAttemptedFrames < current.AtrAttemptedFrames ||
            next.AtrAcceptedFrames < current.AtrAcceptedFrames ||
            next.QhyAttemptedFrames < current.QhyAttemptedFrames ||
            next.QhyAcceptedFrames < current.QhyAcceptedFrames)
        {
            throw new InvalidOperationException("Observation frame counters cannot decrease.");
        }

        var currentAdditional = current.Additional ?? new Dictionary<string, long>();
        var nextAdditional = next.Additional ?? new Dictionary<string, long>();
        foreach (var pair in currentAdditional)
        {
            if (!nextAdditional.TryGetValue(pair.Key, out var value) || value < pair.Value)
            {
                throw new InvalidOperationException($"Observation counter '{pair.Key}' cannot disappear or decrease.");
            }
        }
    }

    private static void ValidateCounters(ObservationRunCounters counters)
    {
        if (counters.AtrAttemptedFrames < 0 || counters.AtrAcceptedFrames < 0 ||
            counters.QhyAttemptedFrames < 0 || counters.QhyAcceptedFrames < 0 ||
            counters.AtrAcceptedFrames > counters.AtrAttemptedFrames ||
            counters.QhyAcceptedFrames > counters.QhyAttemptedFrames)
        {
            throw new ArgumentOutOfRangeException(nameof(counters), "Accepted/attempted frame counters are inconsistent.");
        }

        if (counters.Additional is null) return;
        foreach (var pair in counters.Additional)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(counters), "Additional counters require a name and non-negative value.");
            }
        }
    }

    private static bool CountersEqual(ObservationRunCounters left, ObservationRunCounters right)
    {
        if (left.AtrAttemptedFrames != right.AtrAttemptedFrames ||
            left.AtrAcceptedFrames != right.AtrAcceptedFrames ||
            left.QhyAttemptedFrames != right.QhyAttemptedFrames ||
            left.QhyAcceptedFrames != right.QhyAcceptedFrames)
        {
            return false;
        }

        var leftAdditional = left.Additional ?? new Dictionary<string, long>();
        var rightAdditional = right.Additional ?? new Dictionary<string, long>();
        return leftAdditional.Count == rightAdditional.Count &&
               leftAdditional.All(pair => rightAdditional.TryGetValue(pair.Key, out var value) && value == pair.Value);
    }

    private static void ValidateOptionalHash(string? hash, string name, bool required = false)
    {
        if (string.IsNullOrWhiteSpace(hash))
        {
            if (required) throw new ArgumentException($"SHA-256 '{name}' is required.", name);
            return;
        }

        var normalized = NormalizeHash(hash);
        if (normalized.Length != 64 || normalized.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException($"SHA-256 '{name}' must contain exactly 64 hexadecimal characters.", name);
        }
    }

    private static string? NormalizeNullableHash(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : NormalizeHash(value);

    private static string NormalizeHash(string value) =>
        value.Replace("-", string.Empty, StringComparison.Ordinal).Trim().ToUpperInvariant();

    private static bool SameHash(string left, string right) =>
        string.Equals(NormalizeHash(left), NormalizeHash(right), StringComparison.Ordinal);

    private static string ComputeSha256<T>(T value) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions)));

    private static T[] Append<T>(IReadOnlyList<T> values, T value)
    {
        var result = new T[values.Count + 1];
        for (var index = 0; index < values.Count; index++) result[index] = values[index];
        result[^1] = value;
        return result;
    }

    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) =>
        left >= right ? left : right;

    private static bool IsTerminal(ObservationRunState state) =>
        state is ObservationRunState.Completed or ObservationRunState.Cancelled or ObservationRunState.Faulted;
}
