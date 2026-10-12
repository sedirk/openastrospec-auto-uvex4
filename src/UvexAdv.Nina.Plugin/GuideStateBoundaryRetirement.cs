using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using UvexAdv.Observatory;
using UvexAdv.Phd2;

namespace UvexAdv.Nina.Plugin;

internal interface IObservationTerminalGuideCleanup
{
    Task CleanupTerminalGuideStateAsync(string runId, CancellationToken token);
}

internal sealed record GuideBoundaryWitness(bool OwnersIdle, bool ExclusiveRunOwner,
    bool CameraIdle, bool MountStationary, string TelescopeId, string GuideCameraId,
    Phd2AppState Phd2State, bool Phd2IdentityMatches, DateTimeOffset CapturedUtc);

/// <summary>
/// ADR-0027: retire a FINISHED target's discovery handles, never settle a return.
/// The original control envelope and counters are preserved byte for byte. This
/// route is not available to pause/resume or stage recovery. ADR-0032 adds a
/// separately journal-bound, stopped meridian segment retirement entry point.
/// </summary>
internal static class GuideStateBoundaryRetirement
{
    internal static bool IsDifferentLogicalTarget(ObservationRunManifest source, string newRunId,
        ObservationRunLockedMetadata current)
    {
        if (source.ObservationRunId == newRunId) return false;
        var root = source.LockedMetadata.Labels?.GetValueOrDefault("nativeTargetRootRunId");
        return string.IsNullOrWhiteSpace(root) || root != current.Labels?.GetValueOrDefault("nativeTargetRootRunId");
    }

    internal static void Validate(ObservationRunManifest manifest, GuideBoundaryWitness proof, DateTimeOffset now)
    {
        if (manifest.TerminalState is not (ObservationRunState.Completed or ObservationRunState.Faulted or ObservationRunState.Cancelled))
            throw new InvalidOperationException("目标尚未结束；暂停/恢复不能清除运行中的导星记录。");
        var labels = manifest.LockedMetadata.Labels;
        if (!proof.OwnersIdle || !proof.ExclusiveRunOwner || !proof.CameraIdle || !proof.MountStationary ||
            proof.Phd2State != Phd2AppState.Stopped || !proof.Phd2IdentityMatches ||
            string.IsNullOrWhiteSpace(proof.TelescopeId) || string.IsNullOrWhiteSpace(proof.GuideCameraId) ||
            labels?.GetValueOrDefault("telescopeId") != proof.TelescopeId ||
            labels?.GetValueOrDefault("g3CameraId") != proof.GuideCameraId ||
            proof.CapturedUtc > now || now - proof.CapturedUtc > TimeSpan.FromSeconds(5))
            throw new InvalidOperationException("未确认原设备所有者、停止或新鲜读回；旧记录保留，不授权下一目标。");
    }

    internal static async Task<IReadOnlyList<string>> RetireAsync(string root, string runId,
        Func<CancellationToken, Task<GuideBoundaryWitness>> readWitness, CancellationToken token)
        => await RetireCoreAsync(root, runId, readWitness, token, null).ConfigureAwait(false);

    internal static async Task<IReadOnlyList<string>> RetireMeridianSegmentAsync(string root, string runId,
        string targetRoot, string meridianJournalPath,
        Func<CancellationToken, Task<GuideBoundaryWitness>> readWitness, CancellationToken token)
    {
        void Authorize(ObservationRunManifest manifest)
        {
            var entry = JsonSerializer.Deserialize<NativeMeridianJournal>(File.ReadAllText(meridianJournalPath));
            if (manifest.TerminalState != ObservationRunState.Completed ||
                string.IsNullOrWhiteSpace(targetRoot) ||
                manifest.LockedMetadata.Labels?.GetValueOrDefault("nativeTargetRootRunId") != targetRoot ||
                entry is not { Phase: "OwnersIdle", BeforeSide: "pierEast" or "pierWest",
                    Reason: "Scheduled" or "ObservedSideChange" } || entry.RunId != runId ||
                entry.Segments is not { Count: 1 } || entry.Segments[0] != runId || targetRoot != runId)
                throw new InvalidOperationException("没有本目标唯一翻转交接的已停止分段凭据；旧运动记录保留。");
        }
        return await RetireCoreAsync(root, runId, readWitness, token, Authorize).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<string>> RetireCoreAsync(string root, string runId,
        Func<CancellationToken, Task<GuideBoundaryWitness>> readWitness, CancellationToken token,
        Action<ObservationRunManifest>? authorizeMeridian)
    {
        if (string.IsNullOrWhiteSpace(runId) || Path.GetFileName(runId) != runId || runId is "." or "..")
            throw new InvalidOperationException("无效运行路径。");
        var directory = Path.GetFullPath(Path.Combine(root, runId, "control"));
        var manifestPath = Path.Combine(root, runId, "manifest.json");
        var store = new ObservationRunJournalStore(manifestPath);
        var manifest = await store.ReadAsync(token) ?? throw new InvalidOperationException("原运行清单缺失。");
        if (manifest.ObservationRunId != runId) throw new InvalidOperationException("原运行身份不一致。");
        authorizeMeridian?.Invoke(manifest);
        var files = new List<(string Path, byte[] Bytes)>();
        foreach (var name in new[] { "phd2-lock-shift-pending.json", "g3-acquisition-motion.json" })
        {
            var path = Path.Combine(directory, name);
            if (!File.Exists(path)) continue;
            var bytes = await File.ReadAllBytesAsync(path, token);
            string stateRun, action, preset;
            if (name.StartsWith("phd2", StringComparison.Ordinal))
            {
                var loaded = await Phd2LockShiftPendingStore.LoadAsync(path, token);
                var state = loaded.State ?? throw new InvalidOperationException(loaded.Error ?? "PHD2 记录缺失。");
                stateRun = state.ObservationRunId; action = state.ActionConfigurationSha256; preset = state.CommissioningPresetSha256;
            }
            else
            {
                var loaded = await G3AcquisitionMotionStore.LoadAsync(path, token);
                var state = loaded.State ?? throw new InvalidOperationException(loaded.Error ?? "G3 记录缺失。");
                stateRun = state.ObservationRunId; action = state.ActionConfigurationSha256; preset = state.CommissioningPresetSha256;
            }
            if (stateRun != runId || !string.Equals(action,
                    manifest.LockedMetadata.AdditionalHashes?.GetValueOrDefault("actionConfigurationSha256"), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(preset, manifest.LockedMetadata.CommissioningPresetSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("旧导星记录与原运行绑定不一致，未归档。");
            files.Add((path, bytes));
        }
        // Validate all envelopes before retiring any; fresh idle proof is still
        // mandatory on an empty/idempotent call before the caller drops caches.
        Validate(manifest, await readWitness(token), DateTimeOffset.UtcNow);
        var archived = new List<string>();
        foreach (var file in files)
        {
            var proof = await readWitness(token);
            Validate(manifest, proof, DateTimeOffset.UtcNow);
            var fresh = await store.ReadAsync(token);
            if (fresh?.Revision != manifest.Revision || fresh.TerminalState != manifest.TerminalState ||
                !file.Bytes.SequenceEqual(await File.ReadAllBytesAsync(file.Path, token)))
                throw new InvalidOperationException("归档期间原运行记录改变；保留未处理记录。");
            authorizeMeridian?.Invoke(fresh);
            var purpose = authorizeMeridian is null ? "terminal" : "meridian";
            var archive = file.Path + $".{purpose}-{Guid.NewGuid():N}.archive";
            await using (var stream = new FileStream(archive + ".audit.json", FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                await JsonSerializer.SerializeAsync(stream, new
                {
                    action = authorizeMeridian is null ? "TerminalTargetGuideStateRetired" : "StoppedMeridianSegmentGuideStateRetired",
                    runId, manifest.TerminalState,
                    originalPath = file.Path, archivePath = archive,
                    originalSha256 = Convert.ToHexString(SHA256.HashData(file.Bytes)), proof,
                    returnVerified = false, targetOnSlitVerified = false, motionSent = false,
                    countersReset = false, calibrationChanged = false,
                }, new JsonSerializerOptions { WriteIndented = true }, token);
                stream.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            // Only the authenticated CONTROL handle is renamed, never a raw
            // frame, journal or scientific product. A crash leaves either name.
            File.Move(file.Path, archive, overwrite: false);
            archived.Add(archive);
        }
        return archived;
    }
}
