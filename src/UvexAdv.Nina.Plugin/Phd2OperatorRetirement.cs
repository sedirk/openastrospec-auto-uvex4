using System.IO;
using System.Text.Json;
using UvexAdv.Observatory;
using UvexAdv.Phd2;

namespace UvexAdv.Nina.Plugin;

// Explicit human abandonment, not automatic recovery, return verification or
// calibration authority. This route deliberately cannot be called by Resume.
internal sealed record Phd2OperatorRetirementWitness(
    bool OperatorConfirmed, bool AutomationIdle, bool CameraIdle,
    bool MountConnected, bool MountStationary, string TelescopeId,
    Phd2AppState Phd2State, bool Phd2IdentityMatches, DateTimeOffset CapturedUtc);

internal static class Phd2OperatorRetirement
{
    internal static void Validate(Phd2LockShiftPendingState state,
        ObservationRunState? terminalState, string sourceRunId, string sourceTelescopeId,
        Phd2OperatorRetirementWitness witness, DateTimeOffset nowUtc)
    {
        if (!witness.OperatorConfirmed || !witness.AutomationIdle || !witness.CameraIdle ||
            !witness.MountConnected || !witness.MountStationary ||
            witness.Phd2State != Phd2AppState.Stopped || !witness.Phd2IdentityMatches ||
            string.IsNullOrWhiteSpace(sourceTelescopeId) || witness.TelescopeId != sourceTelescopeId ||
            witness.CapturedUtc > nowUtc || nowUtc - witness.CapturedUtc > TimeSpan.FromSeconds(5))
            throw new InvalidOperationException("人工接管需要明确确认、已结束的自动运行、空闲相机、静止的原赤道仪和已停止的原 PHD2 配置；不会自动停止或移动设备。");
        if (state.Validate().Count != 0 || terminalState != ObservationRunState.Cancelled ||
            sourceRunId != state.ObservationRunId || state.UpdatedUtc > nowUtc ||
            state.Phase is Phd2LockShiftPendingPhase.RetiredCancelledObservation or Phd2LockShiftPendingPhase.SettledBudgetLedger)
            throw new InvalidOperationException("只能显式接管已取消运行的有效未结入缝记录；不能改写活动、已结算或已归档记录。");
    }

    internal static async Task<string> RetireAsync(string observationsRoot,
        Phd2LockShiftPendingFileResult selected, Phd2OperatorRetirementWitness witness,
        CancellationToken cancellationToken)
    {
        var state = selected.State ?? throw new InvalidOperationException("入缝记录缺失。");
        var canonical = Path.GetFullPath(Path.Combine(observationsRoot, state.ObservationRunId,
            "control", "phd2-lock-shift-pending.json"));
        var root = Path.GetFullPath(observationsRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (Path.GetFileName(state.ObservationRunId) != state.ObservationRunId ||
            !canonical.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFullPath(selected.Path), canonical, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("入缝记录不在所属运行的 canonical 路径，未接管。");
        var discovered = await Phd2LockShiftPendingStore.DiscoverAsync(observationsRoot, cancellationToken);
        if (discovered.Any(x => x.Error is not null || x.State is null) ||
            discovered.Count(x => x.State!.Phase != Phd2LockShiftPendingPhase.SettledBudgetLedger) != 1)
            throw new InvalidOperationException("未结入缝记录不唯一或不可读，未接管。");
        var current = await Phd2LockShiftPendingStore.LoadAsync(canonical, cancellationToken);
        if (current.Error is not null || current.State != state)
            throw new InvalidOperationException("确认期间入缝记录已改变，未接管。");
        var manifest = await new ObservationRunJournalStore(Path.Combine(observationsRoot,
            state.ObservationRunId, "manifest.json")).ReadAsync(cancellationToken)
            ?? throw new InvalidOperationException("原运行清单缺失。");
        var metadata = manifest.LockedMetadata;
        if (metadata.AdditionalHashes is null ||
            !metadata.AdditionalHashes.TryGetValue("actionConfigurationSha256", out var actionHash) ||
            !string.Equals(actionHash, state.ActionConfigurationSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(metadata.CommissioningPresetSha256, state.CommissioningPresetSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("原运行清单与动作/标定绑定不一致。");
        var telescopeId = metadata.Labels?.GetValueOrDefault("telescopeId") ?? "";
        var now = DateTimeOffset.UtcNow;
        Validate(state, manifest.TerminalState, manifest.ObservationRunId, telescopeId, witness, now);
        var audit = Path.Combine(Path.GetDirectoryName(canonical)!,
            $"phd2-operator-takeover-{now:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}.json");
        // Preserve the entire pre-transition state before marking it historical.
        // Failure before the atomic state write leaves the pending debt intact.
        await using (var stream = new FileStream(audit, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            await JsonSerializer.SerializeAsync(stream, new
            {
                priorState = state, witness, manifest.ObservationRunId,
                manifest.TerminalState, manifest.PlanSha256, manifest.LockedMetadataSha256,
                retiredAsIncomplete = true, returnVerified = false, targetOnSlitVerified = false,
                motionSent = false, calibrationGranted = false, oldCountersReset = false,
            }, new JsonSerializerOptions { WriteIndented = true }, cancellationToken);
            stream.Flush(flushToDisk: true);
        }
        await Phd2LockShiftPendingStore.WriteAtomicAsync(canonical, state with
        {
            Phase = Phd2LockShiftPendingPhase.RetiredCancelledObservation,
            UpdatedUtc = now,
            LastReason = $"Explicit operator takeover: incomplete, NOT returned or accepted. Original counters/endpoints retained. Audit: {audit}",
        }, cancellationToken);
        return audit;
    }
}
