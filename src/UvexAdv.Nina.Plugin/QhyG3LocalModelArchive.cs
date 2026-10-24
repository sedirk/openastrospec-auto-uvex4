using System.IO;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

internal sealed record QhyG3LocalArchiveEnvelope(int SchemaVersion, string RecordKind,
    string HardwareFingerprintSha256, QhyToG3TransferCandidate Candidate, QhyG3LocalOpticalState OpticalState,
    string OpticalStateSha256, string FocusEvidencePath, string FocusEvidenceSha256);

internal sealed record QhyG3LocalArchiveLoadResult(IReadOnlyList<QhyG3VerifiedLocalSample> Samples,
    IReadOnlyList<string> RejectedRecords, bool IntegrityFailure)
{
    public IReadOnlyDictionary<string, string> SourceFiles { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// Bounded, read-only loading of a known hardware bucket, not latest-index activation.
/// Schema 1 lacked run-local focus provenance and deliberately remains diagnostic only.
/// </summary>
internal static class QhyG3LocalModelArchive
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private const int MaximumRecords = 256;
    private const long MaximumJsonBytes = 2 * 1024 * 1024;

    internal static async Task<string> SaveAsync(string calibrationRoot, QhyToG3TransferCandidate candidate,
        QhyG3LocalOpticalState opticalState, string focusEvidencePath, string focusEvidenceSha256,
        CancellationToken token)
    {
        if (candidate.ValidateIntegrity().Count != 0 || !opticalState.IsComplete)
            throw new InvalidDataException("QHY/G3 local candidate or optical state is invalid.");
        await VerifyFileAsync(focusEvidencePath, focusEvidenceSha256, token).ConfigureAwait(false);
        await VerifyFocusEvidenceAsync(focusEvidencePath, candidate, opticalState, token).ConfigureAwait(false);
        var binding = BindingFor(candidate, opticalState);
        var fingerprint = QhyG3LocalModelPolicy.HardwareFingerprint(binding);
        var directory = Path.Combine(Path.GetFullPath(calibrationRoot), fingerprint[..24]);
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory,
            $"local-candidate-{candidate.CreatedUtc.UtcDateTime:yyyyMMddTHHmmssfffZ}-{candidate.CandidateSha256[..24].ToLowerInvariant()}.json");
        var envelope = new QhyG3LocalArchiveEnvelope(2, "QhyG3FocusBoundLocalCandidate", fingerprint, candidate,
            opticalState, opticalState.ComputeSha256(), Path.GetFullPath(focusEvidencePath), focusEvidenceSha256);
        if (File.Exists(destination))
        {
            var existing = await ReadAsync<QhyG3LocalArchiveEnvelope>(destination, token).ConfigureAwait(false);
            if (JsonSerializer.Serialize(existing, Options) != JsonSerializer.Serialize(envelope, Options))
                throw new InvalidDataException("An immutable local candidate already exists with different focus provenance.");
            return destination;
        }
        var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, envelope, Options, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
                stream.Flush(true);
            }
            File.Move(temporary, destination, false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return destination;
    }

    internal static async Task<QhyG3LocalArchiveLoadResult> LoadAsync(string calibrationRoot,
        QhyG3LocalModelBinding binding, DateTimeOffset now, TimeSpan maximumAge, CancellationToken token)
    {
        var fingerprint = QhyG3LocalModelPolicy.HardwareFingerprint(binding);
        var directory = Path.Combine(Path.GetFullPath(calibrationRoot), fingerprint[..24]);
        if (!Directory.Exists(directory)) return new([], [], false);
        if (maximumAge <= TimeSpan.Zero || maximumAge > TimeSpan.FromMinutes(30))
            return new([], ["QHY_G3_LOCAL_ARCHIVE_INVALID_AGE"], true);
        var samples = new List<QhyG3VerifiedLocalSample>();
        var rejected = new List<string>();
        var sourceFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var integrityFailure = false;
        // Filename chronology is generated from immutable UTC creation, not filesystem LastWriteTime.
        // Bound enumeration before parsing or hashing; old schema-1 candidate-* names are not production inputs.
        var files = Directory.EnumerateFiles(directory, "local-candidate-*.json", SearchOption.TopDirectoryOnly)
            .Where(file => InFilenameTimeWindow(file, now, maximumAge))
            .OrderDescending(StringComparer.Ordinal).Take(MaximumRecords + 1).ToArray();
        if (files.Length > MaximumRecords)
            return new([], ["QHY_G3_LOCAL_ARCHIVE_RECORD_LIMIT"], true);
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var archiveSha = await DigestAsync(file, token).ConfigureAwait(false);
                var e = await ReadAsync<QhyG3LocalArchiveEnvelope>(file, token).ConfigureAwait(false);
                if (e.SchemaVersion != 2 || e.RecordKind != "QhyG3FocusBoundLocalCandidate" ||
                    e.HardwareFingerprintSha256 != fingerprint || e.Candidate is null || e.OpticalState is null ||
                    !QhyG3LocalModelPolicy.SameHash(e.OpticalStateSha256, e.OpticalState.ComputeSha256()))
                    throw new InvalidDataException("Envelope schema, hardware or optical-state digest is invalid.");
                var c = e.Candidate;
                if (c.CreatedUtc > now || now - c.CreatedUtc > maximumAge || c.ValidUntilUtc < now ||
                    !QhyG3LocalModelPolicy.SameHash(c.CommissioningPresetSha256, binding.CommissioningPresetSha256) ||
                    !QhyG3LocalModelPolicy.SameHash(c.NightSetupSha256, binding.NightSetupSha256) ||
                    !QhyG3LocalModelPolicy.SameHash(e.OpticalStateSha256, binding.OpticalState.ComputeSha256()))
                {
                    rejected.Add($"{Path.GetFileName(file)}: QHY_G3_LOCAL_ARCHIVE_INAPPLICABLE_TIME_SETUP_OR_FOCUS");
                    continue;
                }
                if (c.ValidateIntegrity().Count != 0 ||
                    !QhyG3LocalModelPolicy.SameHash(QhyG3LocalModelPolicy.HardwareFingerprint(BindingFor(c, e.OpticalState)), fingerprint))
                    throw new InvalidDataException("Candidate integrity or original hardware bucket differs.");
                var rebuilt = QhyG3SolvePairBuilder.Build(new(c.CollectionPolicy, c.ObservationRunId,
                    c.ActionConfigurationSha256, c.CommissioningPresetSha256, c.NightSetupId, c.NightSetupSha256,
                    c.InstallationEpochId, c.TelescopeDeviceId, c.QhyOpticalTrainId, c.G3OpticalTrainId,
                    c.PairSource, c.Qhy, c.G3, c.MountReadbacks, c.CreatedUtc));
                if (rebuilt.Candidate is null || !QhyG3LocalModelPolicy.SameHash(rebuilt.Candidate.CandidateSha256, c.CandidateSha256))
                    throw new InvalidDataException("Candidate semantics cannot be rebuilt from the original pair.");
                await VerifyFileAsync(e.FocusEvidencePath, e.FocusEvidenceSha256, token).ConfigureAwait(false);
                await VerifyFocusEvidenceAsync(e.FocusEvidencePath, c, e.OpticalState, token).ConfigureAwait(false);
                foreach (var frame in new[] { c.Qhy, c.G3 })
                {
                    await VerifyFileAsync(frame.FramePath, frame.FrameSha256, token).ConfigureAwait(false);
                    await VerifyFileAsync(frame.SolveEvidencePath, frame.SolveEvidenceSha256, token).ConfigureAwait(false);
                    await VerifyFormalSolveAsync(frame, c, token).ConfigureAwait(false);
                }
                await VerifyManifestReferencesAsync(e, token).ConfigureAwait(false);
                sourceFiles[file] = archiveSha;
                sourceFiles[e.FocusEvidencePath] = e.FocusEvidenceSha256;
                foreach (var frame in new[] { c.Qhy, c.G3 })
                {
                    sourceFiles[frame.FramePath] = frame.FrameSha256;
                    sourceFiles[frame.SolveEvidencePath] = frame.SolveEvidenceSha256;
                }
                samples.Add(new(c, e.OpticalState, e.FocusEvidencePath, e.FocusEvidenceSha256));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
                InvalidDataException or ArgumentException or KeyNotFoundException or InvalidOperationException)
            {
                integrityFailure = true;
                rejected.Add($"{Path.GetFileName(file)}: QHY_G3_LOCAL_ARCHIVE_INTEGRITY_INVALID: {ex.Message}");
            }
        }
        foreach (var (path, sha) in sourceFiles)
        {
            try { await VerifyFileAsync(path, sha, token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                integrityFailure = true;
                rejected.Add($"QHY_G3_LOCAL_ARCHIVE_SOURCE_CHANGED_DURING_READ: {ex.Message}");
            }
        }
        // Do not create a seemingly better model by throwing away a corrupt applicable source.
        return new(integrityFailure ? [] : samples.AsReadOnly(), rejected.AsReadOnly(), integrityFailure)
        { SourceFiles = integrityFailure ? new Dictionary<string, string>() : sourceFiles };
    }

    internal static QhyG3LocalModelBinding BindingFor(QhyToG3TransferCandidate c, QhyG3LocalOpticalState state) => new(
        c.CommissioningPresetSha256, c.NightSetupId, c.NightSetupSha256, c.InstallationEpochId,
        c.TelescopeDeviceId, c.QhyCameraStableId, c.G3CameraStableId, c.QhyOpticalTrainId,
        c.G3OpticalTrainId, c.PierSide, c.G3.FrameWidthPixels, c.G3.FrameHeightPixels, c.G3.BinningX, state);

    private static async Task VerifyFocusEvidenceAsync(string path, QhyToG3TransferCandidate c,
        QhyG3LocalOpticalState state, CancellationToken token)
    {
        using var doc = await ReadDocumentAsync(path, token).ConfigureAwait(false);
        var root = doc.RootElement;
        if (root.GetProperty("observationRunId").GetString() != c.ObservationRunId ||
            !QhyG3LocalModelPolicy.SameHash(root.GetProperty("actionConfigurationSha256").GetString(), c.ActionConfigurationSha256))
            throw new InvalidDataException("Focus evidence does not belong to the source run/action.");
        var optical = root.GetProperty("payload").GetProperty("opticalState").Deserialize<QhyG3LocalOpticalState>(Options);
        if (!QhyG3LocalModelPolicy.SameHash(root.GetProperty("payload").GetProperty("candidateSha256").GetString(), c.CandidateSha256))
            throw new InvalidDataException("Focus evidence is not bound to this exact pair candidate.");
        if (optical is null || !optical.IsComplete || !QhyG3LocalModelPolicy.SameHash(optical.ComputeSha256(), state.ComputeSha256()))
            throw new InvalidDataException("Focus evidence payload differs from the archived optical state.");
    }

    private static async Task VerifyFormalSolveAsync(QhyG3SolvedFrame frame, QhyToG3TransferCandidate c, CancellationToken token)
    {
        using var doc = await ReadDocumentAsync(frame.SolveEvidencePath, token).ConfigureAwait(false);
        var root = doc.RootElement;
        var payload = root.GetProperty("payload");
        var solved = payload.GetProperty("solved");
        if (root.GetProperty("kind").GetString() != "plate-solve-evidence" ||
            root.GetProperty("observationRunId").GetString() != c.ObservationRunId ||
            !QhyG3LocalModelPolicy.SameHash(root.GetProperty("actionConfigurationSha256").GetString(), c.ActionConfigurationSha256) ||
            !payload.GetProperty("success").GetBoolean() || !payload.GetProperty("sourceUnchanged").GetBoolean() ||
            !QhyG3LocalModelPolicy.SameHash(root.GetProperty("source").GetProperty("sha256").GetString(), frame.FrameSha256) ||
            !SamePath(root.GetProperty("source").GetProperty("absolutePath").GetString(), frame.FramePath) ||
            Math.Abs(solved.GetProperty("raDegrees").GetDouble() - frame.CenterRightAscensionDegrees) > 1e-9 ||
            Math.Abs(solved.GetProperty("decDegrees").GetDouble() - frame.CenterDeclinationDegrees) > 1e-9 ||
            Math.Abs(solved.GetProperty("pixelScaleArcseconds").GetDouble() - frame.PixelScaleArcseconds) > 1e-9 ||
            Math.Abs(Math.IEEERemainder(solved.GetProperty("positionAngleDegrees").GetDouble() - frame.PositionAngleDegrees, 360)) > 1e-9 ||
            solved.GetProperty("flipped").GetBoolean() != frame.Flipped)
            throw new InvalidDataException("A source is not the referenced unchanged formal solve or its solved geometry differs.");
    }

    private static async Task VerifyManifestReferencesAsync(QhyG3LocalArchiveEnvelope e, CancellationToken token)
    {
        var evidenceDirectory = Path.GetDirectoryName(Path.GetFullPath(e.FocusEvidencePath))!;
        var runDirectory = Directory.GetParent(evidenceDirectory)?.FullName ?? throw new InvalidDataException("Missing source run directory.");
        if (Path.GetFileName(evidenceDirectory) != "evidence" || Path.GetFileName(runDirectory) != e.Candidate.ObservationRunId)
            throw new InvalidDataException("Focus evidence is outside the source run evidence directory.");
        using var doc = await ReadDocumentAsync(Path.Combine(runDirectory, "manifest.json"), token).ConfigureAwait(false);
        var root = doc.RootElement;
        var c = e.Candidate;
        if (root.GetProperty("observationRunId").GetString() != c.ObservationRunId ||
            !QhyG3LocalModelPolicy.SameHash(root.GetProperty("lockedMetadata").GetProperty("commissioningPresetSha256").GetString(), c.CommissioningPresetSha256) ||
            !QhyG3LocalModelPolicy.SameHash(root.GetProperty("lockedMetadata").GetProperty("nightSetupSha256").GetString(), c.NightSetupSha256))
            throw new InvalidDataException("Source manifest configuration differs from the immutable candidate.");
        var references = new[] { (e.FocusEvidencePath, e.FocusEvidenceSha256),
            (c.Qhy.SolveEvidencePath, c.Qhy.SolveEvidenceSha256), (c.G3.SolveEvidencePath, c.G3.SolveEvidenceSha256) };
        foreach (var (path, sha) in references)
            if (!root.GetProperty("evidence").EnumerateArray().Any(r =>
                SamePath(r.GetProperty("absolutePath").GetString(), path) &&
                QhyG3LocalModelPolicy.SameHash(r.GetProperty("sha256").GetString(), sha)))
                throw new InvalidDataException("A source/focus evidence hash is not indexed by its originating run manifest.");
    }

    private static bool SamePath(string? a, string b) => !string.IsNullOrWhiteSpace(a) &&
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
    private static bool InFilenameTimeWindow(string file, DateTimeOffset now, TimeSpan age)
    {
        var name = Path.GetFileName(file);
        const string prefix = "local-candidate-";
        // Malformed current records are parsed/rejected explicitly, not silently hidden.
        return name.Length < prefix.Length + 19 || !DateTimeOffset.TryParseExact(name.Substring(prefix.Length, 19),
            "yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var created) ||
            created >= now - age && created <= now;
    }
    private static async Task VerifyFileAsync(string path, string expected, CancellationToken token)
    {
        if (!QhyG3LocalModelPolicy.IsSha(expected)) throw new InvalidDataException("Missing source SHA-256.");
        var actual = await DigestAsync(path, token).ConfigureAwait(false);
        if (!QhyG3LocalModelPolicy.SameHash(actual, expected)) throw new InvalidDataException("A referenced source SHA-256 changed.");
    }
    private static async Task<string> DigestAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
    }
    private static async Task<T> ReadAsync<T>(string path, CancellationToken token)
    {
        using var doc = await ReadDocumentAsync(path, token).ConfigureAwait(false);
        return doc.RootElement.Deserialize<T>(Options) ?? throw new InvalidDataException("Null JSON record.");
    }
    private static async Task<JsonDocument> ReadDocumentAsync(string path, CancellationToken token)
    {
        var info = new FileInfo(path);
        if (info.Length > MaximumJsonBytes || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("Oversized or redirected local provenance record.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
    }
}
