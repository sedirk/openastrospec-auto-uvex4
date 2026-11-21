using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

/// <summary>
/// ADR-0050: abandon an ended segment's unissued flip, never resume it.
/// This capability has no device-command port. The startup workflow supplies
/// the same original-owner, safety, stopped and fresh-frame proof as ADR-0027.
/// </summary>
internal sealed record PreDispatchMeridianRetirement(string Path, byte[] Bytes, NativeMeridianJournal Entry)
{
    internal static async Task<PreDispatchMeridianRetirement?> DiscoverAsync(string root, CancellationToken token)
    {
        if (!Directory.Exists(root)) return null;
        var canonical = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, "control", "native-meridian-flip.json"));
        PreDispatchMeridianRetirement? result = null;
        foreach (var path in Directory.EnumerateFiles(root, "native-meridian-flip.json", SearchOption.AllDirectories))
        {
            token.ThrowIfCancellationRequested();
            if (NativeMeridianJournalStore.PendingGate(path) is not { } gate) continue;
            if (result is not null || !string.Equals(System.IO.Path.GetFullPath(path), canonical, StringComparison.OrdinalIgnoreCase))
                throw Rejected("翻转记录位置不唯一或不在原全局控制路径；未归档。");
            var bytes = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
            NativeMeridianJournal? entry;
            try { entry = JsonSerializer.Deserialize<NativeMeridianJournal>(bytes); }
            catch (JsonException) { throw new PhysicalActionGateException(gate); }
            if (entry is not { Phase: "OwnersIdle" or "WaitingForMeridian", BeforeSide: "pierEast" or "pierWest", AfterSide: "" } ||
                entry.Reason is not ("Scheduled" or "ObservedSideChange") ||
                entry.Phase == "WaitingForMeridian" && entry.Reason != "Scheduled" ||
                !IsSafeRunId(entry.RunId) ||
                !HasAuthenticatedSegmentShape(entry) ||
                entry.UpdatedUtc == default || entry.UpdatedUtc > DateTimeOffset.UtcNow)
                throw new PhysicalActionGateException(gate);
            result = new(path, bytes, entry);
        }
        return result;
    }

    internal void ValidateSource(ObservationRunManifest manifest, string newRunId, ObservationRunLockedMetadata current)
    {
        var rootRunId = Entry.Segments[0];
        var priorSegments = Entry.Segments.Take(Entry.Segments.Count - 1).ToArray();
        if (!HasAuthenticatedSegmentShape(Entry) ||
            manifest.ObservationRunId != Entry.RunId || manifest.TerminalState != ObservationRunState.Completed ||
            manifest.Snapshot.State != ObservationRunState.Completed ||
            manifest.LockedMetadata.Labels?.GetValueOrDefault("nativeTargetRootRunId") != rootRunId ||
            !PriorSegmentsMatch(manifest.LockedMetadata.Labels?.GetValueOrDefault("nativeTargetPriorSegments"), priorSegments) ||
            !GuideStateBoundaryRetirement.IsDifferentLogicalTarget(manifest, newRunId, current) ||
            !manifest.Gates.Values.Any(x => x.Code == "NATIVE_MERIDIAN_SEGMENT_BOUNDARY" && x.Disposition == GateDisposition.Passed) ||
            Entry.UpdatedUtc < manifest.Snapshot.UpdatedUtc)
            throw Rejected("旧记录不是已提交的认证中天分段，或仍是同一逻辑目标；不自动恢复旧段。");

        var original = manifest.LockedMetadata;
        foreach (var name in new[] { "atrCameraId", "g3CameraId", "telescopeId", "qhyCameraId",
                     "phd2RuntimeCameraName", "phd2RuntimeMountName" })
            if (string.IsNullOrWhiteSpace(original.Labels?.GetValueOrDefault(name)) ||
                original.Labels[name] != current.Labels?.GetValueOrDefault(name))
                throw Rejected("原设备所有者绑定改变或缺失，当前设备不能证明原所有者已停止。");
        if (string.IsNullOrWhiteSpace(original.Phd2ProfileEvidenceSha256) ||
            !string.Equals(original.Phd2ProfileEvidenceSha256, current.Phd2ProfileEvidenceSha256, StringComparison.OrdinalIgnoreCase))
            throw Rejected("原 PHD2 Profile 绑定改变或缺失；未归档翻转意图。");
        foreach (var metadata in new[] { original, current })
            if (metadata.Labels?.GetValueOrDefault("weakSupervisionEnabled") != "False" ||
                metadata.Labels.GetValueOrDefault("requireSafetyMonitor") != "True" ||
                metadata.Labels.GetValueOrDefault("requireOpenDomeOrRoof") != "True")
                throw Rejected("未发令交接恢复必须保持完整原生安全链，不能降级为弱监督。");
    }

    internal async Task RequireUnchangedAsync(string root, ObservationRunManifest source, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!File.Exists(Path) || !Bytes.SequenceEqual(await File.ReadAllBytesAsync(Path, token).ConfigureAwait(false)))
            throw Rejected("核验期间翻转记录改变或丢失；未授权归档或继续。");
        var fresh = await new ObservationRunJournalStore(System.IO.Path.Combine(root, Entry.RunId, "manifest.json"))
            .ReadAsync(token).ConfigureAwait(false);
        if (fresh is null || fresh.Revision != source.Revision || fresh.TerminalState != source.TerminalState ||
            fresh.PlanSha256 != source.PlanSha256 || fresh.LockedMetadataSha256 != source.LockedMetadataSha256)
            throw Rejected("核验期间原观测段改变；未授权归档或继续。");
    }

    internal async Task<string> ArchiveAsync(string root, string newRunId, ObservationRunManifest source,
        Func<CancellationToken, Task<GuideBoundaryWitness>> readWitness, CancellationToken token)
    {
        var proof = await readWitness(token).ConfigureAwait(false);
        GuideStateBoundaryRetirement.Validate(source, proof, DateTimeOffset.UtcNow);
        await RequireUnchangedAsync(root, source, token).ConfigureAwait(false);
        var archive = Path + $".pre-dispatch-{Guid.NewGuid():N}.archive";
        await using (var stream = new FileStream(archive + ".audit.json", FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            await JsonSerializer.SerializeAsync(stream, new
            {
                action = "EndedPreDispatchMeridianIntentRetired", oldRunId = Entry.RunId,
                targetRootRunId = Entry.Segments[0], segments = Entry.Segments, newRunId,
                originalPath = Path, archivePath = archive, originalSha256 = Convert.ToHexString(SHA256.HashData(Bytes)),
                source.Revision, source.PlanSha256, source.LockedMetadataSha256, source.Counters, Entry.Phase, proof,
                motionSent = false, flipVerified = false, returnVerified = false, targetOnSlitVerified = false,
                countersReset = false, continuedOldTarget = false,
            }, new JsonSerializerOptions { WriteIndented = true }, token).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        // Archive the flip handle LAST. A crash/cancel before this move leaves
        // an outstanding intent, even if guide handles were already archived.
        GuideStateBoundaryRetirement.Validate(source, await readWitness(token).ConfigureAwait(false), DateTimeOffset.UtcNow);
        await RequireUnchangedAsync(root, source, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        File.Move(Path, archive, overwrite: false);
        return archive;
    }

    private static bool HasAuthenticatedSegmentShape(NativeMeridianJournal entry)
    {
        if (entry.Segments is not { Count: 1 or 2 } segments ||
            segments.Any(x => !IsSafeRunId(x)) ||
            segments.Distinct(StringComparer.Ordinal).Count() != segments.Count ||
            segments[^1] != entry.RunId)
            return false;
        return segments.Count == 1 || segments[0] != segments[1];
    }

    private static bool IsSafeRunId(string? runId) =>
        !string.IsNullOrWhiteSpace(runId) &&
        System.IO.Path.GetFileName(runId) == runId &&
        runId is not "." or "..";

    private static bool PriorSegmentsMatch(string? serialized, IReadOnlyList<string> expected)
    {
        if (expected.Count == 0) return string.IsNullOrWhiteSpace(serialized) || serialized == "[]";
        if (string.IsNullOrWhiteSpace(serialized)) return false;
        try
        {
            return JsonSerializer.Deserialize<string[]>(serialized)
                ?.SequenceEqual(expected, StringComparer.Ordinal) == true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static PhysicalActionGateException Rejected(string message) =>
        new(GateResult.Unknown("NATIVE_FLIP_UNCONFIRMED", message));
}
