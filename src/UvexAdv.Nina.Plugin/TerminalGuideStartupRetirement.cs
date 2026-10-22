using System.IO;
using System.Runtime.Versioning;
using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

/// <summary>
/// A new explicit target may abandon ONE terminal target's authenticated guide
/// handles after a stationary fresh-frame/owner boundary. There is deliberately
/// no motion, solve, return or budget-authority port in this workflow.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class TerminalGuideStartupRetirement
{
    internal static async Task<IReadOnlyList<string>?> TryRetireAsync(string root, string newRunId,
        ObservationRunLockedMetadata current,
        Func<CancellationToken, Task> prepareOwners,
        Func<CancellationToken, Task> captureFreshStationaryFrame,
        Func<CancellationToken, Task<GuideBoundaryWitness>> readWitness,
        Func<CancellationToken, Task> checkSafety, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var g3 = await G3AcquisitionMotionStore.DiscoverAsync(root, token).ConfigureAwait(false);
        var phd = await Phd2LockShiftPendingStore.DiscoverAsync(root, token).ConfigureAwait(false);
        if (g3.Any(x => x.State is null || x.Error is not null) || phd.Any(x => x.State is null || x.Error is not null))
            throw Rejected("GUIDE_STARTUP_EVIDENCE_UNREADABLE", "旧导星控制记录不可认证，未连接或归档。");
        var outstanding = g3.Where(x => x.State!.Phase != G3AcquisitionMotionPhase.SettledBudgetLedger)
            .Select(x => x.State!.ObservationRunId)
            .Concat(phd.Where(x => x.State!.Phase is not (Phd2LockShiftPendingPhase.SettledBudgetLedger or Phd2LockShiftPendingPhase.RetiredCancelledObservation))
                .Select(x => x.State!.ObservationRunId)).Distinct(StringComparer.Ordinal).ToArray();
        if (outstanding.Length == 0) return null;
        if (outstanding.Length != 1)
            throw Rejected("GUIDE_STARTUP_MULTIPLE_RESPONSIBILITIES", "存在多个未结导星目标；不能猜测应放弃哪一条责任。");
        var sourceId = outstanding[0];
        if (Path.GetFileName(sourceId) != sourceId || sourceId is "." or "..")
            throw Rejected("GUIDE_STARTUP_PATH_INVALID", "旧目标控制路径无效。");
        var manifest = await new ObservationRunJournalStore(Path.Combine(root, sourceId, "manifest.json"))
            .ReadAsync(token).ConfigureAwait(false);
        if (manifest is null || manifest.ObservationRunId != sourceId)
            throw Rejected("GUIDE_STARTUP_MANIFEST_MISSING", "旧目标原始清单缺失或身份不一致。");
        if (manifest.TerminalState is not (ObservationRunState.Completed or ObservationRunState.Faulted or ObservationRunState.Cancelled) ||
            !GuideStateBoundaryRetirement.IsDifferentLogicalTarget(manifest, newRunId, current))
            return null; // Pause/resume/same logical target retain the exact old recovery route and debits.
        // A changed science/focus preset is harmless to retirement; changing the
        // remote physical owner is not. The current endpoint cannot attest that
        // another, formerly bound QHY owner has stopped.
        if (!string.IsNullOrWhiteSpace(manifest.LockedMetadata.QhyConfigurationSha256) &&
                !string.Equals(manifest.LockedMetadata.QhyConfigurationSha256, current.QhyConfigurationSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrWhiteSpace(manifest.LockedMetadata.Labels?.GetValueOrDefault("qhyCameraId")) &&
                manifest.LockedMetadata.Labels["qhyCameraId"] != current.Labels?.GetValueOrDefault("qhyCameraId"))
            throw Rejected("GUIDE_STARTUP_REMOTE_OWNER_CHANGED", "旧测光所有者绑定改变；当前端点不能证明旧设备已停止。");

        foreach (var item in g3)
        {
            RequireRunPath(root, item.Path, item.State!.ObservationRunId, "g3-acquisition-motion.json");
            var original = await RealObservationStageRunner.ValidateG3AcquisitionMotionManifestAsync(item, token).ConfigureAwait(false);
            if (original.Error is { } error) throw new PhysicalActionGateException(error);
        }
        foreach (var item in phd)
        {
            RequireRunPath(root, item.Path, item.State!.ObservationRunId, "phd2-lock-shift-pending.json");
            var original = await RealObservationStageRunner.ValidatePhd2LockManifestAsync(item, token).ConfigureAwait(false);
            if (original.Error is { } error) throw new PhysicalActionGateException(error);
        }
        async Task CheckIndependentDebt(CancellationToken ct)
        {
            foreach (var debt in await SlitPlacementPendingStore.DiscoverAsync(root, ct).ConfigureAwait(false))
                if (debt.Error is not null || debt.State is null || debt.State.Phase != SlitPlacementPendingPhase.SettledBudgetLedger)
                    throw Rejected("GUIDE_STARTUP_MECHANICAL_DEBT", "仍有独立机构未结责任；导星句柄归档不能代替机械回程。");
            if (Directory.Exists(root))
                foreach (var path in Directory.EnumerateFiles(root, "native-meridian-flip.json", SearchOption.AllDirectories))
                    if (NativeMeridianJournalStore.PendingGate(path) is { } gate) throw new PhysicalActionGateException(gate);
            await checkSafety(ct).ConfigureAwait(false);
        }
        await CheckIndependentDebt(token).ConfigureAwait(false);
        await prepareOwners(token).ConfigureAwait(false);
        await CheckIndependentDebt(token).ConfigureAwait(false);
        GuideStateBoundaryRetirement.Validate(manifest, await readWitness(token).ConfigureAwait(false), DateTimeOffset.UtcNow);
        await captureFreshStationaryFrame(token).ConfigureAwait(false);
        await CheckIndependentDebt(token).ConfigureAwait(false);
        return await GuideStateBoundaryRetirement.RetireAsync(root, sourceId, async ct =>
        {
            await CheckIndependentDebt(ct).ConfigureAwait(false);
            return await readWitness(ct).ConfigureAwait(false);
        }, token).ConfigureAwait(false);
    }

    private static void RequireRunPath(string root, string path, string run, string name)
    {
        if (Path.GetFileName(run) != run || run is "." or ".." ||
            !string.Equals(Path.GetFullPath(path), Path.GetFullPath(Path.Combine(root, run, "control", name)), StringComparison.OrdinalIgnoreCase))
            throw Rejected("GUIDE_STARTUP_PATH_INVALID", "控制记录位置与原运行身份不一致。");
    }

    private static PhysicalActionGateException Rejected(string code, string message) => new(GateResult.Unknown(code, message));
}
