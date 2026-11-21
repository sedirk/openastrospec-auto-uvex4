using System.IO;
using System.Text.Json;
using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

public sealed record NightSafetyStandbySnapshot(string Id, string Phase, string Message,
    string JournalPath, DateTimeOffset? DeadlineUtc, bool PhysicalHardware,
    string? Reason = null, IReadOnlyList<string>? Errors = null)
{
    public bool IsActive => Phase is "Waiting" or "Closing" or "MonitoringOnly" or "CloseoutFailed";
    public string? LatestSafetyReason { get; init; }
    public GateResult? LastHandoverGate { get; init; }
    internal string OperatorStatus => (Reason == WindowsRestartProbe.Code ? "Windows 更新待重启。 " : "") +
        (LastHandoverGate is { Disposition: not GateDisposition.Passed } gate
        ? $"{Message} 最近一次交接：{gate.Message}" : Message);
}

// The observing task has a finite lifetime. This owner has a separate lifetime,
// no acquisition entry point, and exactly one checked closeout attempt.
internal sealed record NightSafetyStandbyRequest(bool Real, string ConfigurationKey,
    DateTimeOffset? DeadlineUtc, string ParentJournal, bool CanHandOver,
    Func<Action, IDisposable?> WatchSafety, Func<GateResult?> StopGate,
    Func<CancellationToken, Task<IReadOnlyList<string>>> Closeout, string? Warning = null,
    Func<CancellationToken, Task<GateResult>>? RecheckIdle = null,
    bool MonitoringOnly = false, string? InitialReason = null,
    Func<CancellationToken, Task<IReadOnlyList<string>>>? RestartCloseout = null);

internal sealed class NightSafetyStandby
{
    private readonly object sync = new();
    private readonly NightSafetyStandbyRequest request;
    private readonly IDisposable reservation;
    private readonly Action<NightSafetyStandbySnapshot> changed;
    private readonly SemaphoreSlim wake = new(0, 1);
    private readonly SemaphoreSlim journalGate = new(1, 1);
    private readonly CancellationTokenSource shutdown = new();
    private IDisposable? subscription;
    private NightSafetyStandbySnapshot snapshot;
    private string? closeReason;
    private string? persistenceError;
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal NightSafetyStandby(NightSafetyStandbyRequest request, IDisposable reservation,
        Action<NightSafetyStandbySnapshot> changed)
    {
        this.request = request; this.reservation = reservation; this.changed = changed;
        var id = $"standby-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}";
        var path = Path.Combine(Path.GetDirectoryName(request.ParentJournal)!, id + ".json");
        closeReason = request.InitialReason;
        snapshot = new(id, request.MonitoringOnly ? "MonitoringOnly" : "Waiting",
            request.MonitoringOnly ? "自动收口未确认：仅保留安全监控与报警，不能保证关顶，请人工处理。" + request.Warning :
                "观测序列已退出；独立安全待机：AIWeather、晨光及原截止仍有效。" + request.Warning,
            path, request.DeadlineUtc, request.Real, request.InitialReason);
    }

    internal NightSafetyStandbySnapshot Snapshot { get { lock (sync) return snapshot; } }
    // Completion is the finite closeout/transfer decision, not the lifetime of
    // monitoring after a failed attempt. Native end instructions must not hang.
    internal Task Completion => completion.Task;
    internal Task LifetimeCompletion { get; private set; } = Task.CompletedTask;

    // Called while the original night still owns the exclusive lease and watch.
    internal async Task ArmAsync()
    {
        subscription = request.WatchSafety(() => RequestCloseout("NIGHT_UNSAFE"));
        try { await SaveAsync().ConfigureAwait(false); }
        catch (Exception ex) when (request.MonitoringOnly)
        {
            // No mechanical action is available in this mode. A full disk must
            // not make us discard the last live safety subscription as well.
            persistenceError = "安全待机日志写入失败：" + ex.Message;
            lock (sync) snapshot = snapshot with
            { Message = snapshot.Message + " " + persistenceError, Errors = new[] { persistenceError } };
        }
        catch { subscription?.Dispose(); subscription = null; throw; }
    }

    internal void Start() => LifetimeCompletion = Task.Run(RunAsync);

    internal bool RequestCloseout(string reason = "OPERATOR_CLOSEOUT")
    {
        lock (sync)
        {
            if (!snapshot.IsActive) return false;
            // An unavailable button/API is not a mechanical request receipt.
            // It must not make an otherwise recoverable monitor-only boundary
            // permanently untransferable. Actual safety reasons still latch.
            if (reason == "OPERATOR_CLOSEOUT" && snapshot.Phase is "MonitoringOnly" or "CloseoutFailed")
            { Wake(); return false; }
            closeReason ??= reason;
            Wake();
            return true;
        }
    }

    // The caller holds the host lock. The same standby lock serializes transfer
    // against weather/deadline closeout. The new watch is armed by commit before
    // this watch or the cross-process lease is released; no unprotected gap.
    internal async Task<bool> RecheckIdleAsync(bool real, string? configurationKey, CancellationToken token)
    {
        GateResult? rejection;
        lock (sync)
        {
            rejection = HandoverRejection(real, configurationKey);
            if (rejection is null && request.CanHandOver) return false; // Original checked boundary is still eligible.
            if (rejection is not null) snapshot = snapshot with { LastHandoverGate = rejection };
        }
        if (rejection is not null) { await PublishAsync().ConfigureAwait(false); return false; }
        // Explicit new-night request only; no background recovery, device
        // connections, motion or cached promotion of a failed boundary.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        GateResult result;
        try { result = await request.RecheckIdle!(timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            result = GateResult.Unknown("NIGHT_STANDBY_IDLE_RECHECK_TIMEOUT",
                "20 秒只读复核内未确认设备停止；请在 N.I.N.A. 连接原绑定设备并停止曝光/导星/运动后重新开始序列。未自动连接或发出机械动作。");
        }
        catch (Exception ex)
        {
            result = GateResult.Unknown("NIGHT_STANDBY_IDLE_RECHECK_FAILED",
                "读取原设备停止状态失败：" + ex.Message + "；请恢复原设备接口后重新开始序列，历史账本保留。");
        }
        lock (sync)
        {
            // A late unsafe/cutoff cannot be replaced by a successful idle check.
            rejection = HandoverRejection(real, configurationKey);
            result = rejection ?? result;
            snapshot = snapshot with { LastHandoverGate = result };
        }
        await PublishAsync().ConfigureAwait(false);
        return result.Disposition == GateDisposition.Passed;
    }

    private GateResult? HandoverRejection(bool real, string? configurationKey)
    {
        if (snapshot.Phase is not ("Waiting" or "MonitoringOnly") || closeReason is not null)
            return GateResult.Unknown("NIGHT_STANDBY_CLOSEOUT_LATCHED",
                $"本待机已记录收口要求（{closeReason ?? snapshot.Reason ?? snapshot.Phase}），不能借重新开始序列撤销；请检查收口诊断并由操作员处理。原截止未延后，未自动重试机械动作。");
        if (request.Real != real || request.ConfigurationKey != (configurationKey ?? string.Empty))
            return GateResult.Unknown("NIGHT_STANDBY_CONFIGURATION_CHANGED",
                "新序列的真实/模拟模式或锁定设备配置与现有安全待机不同；不能转移原安全责任。请先核对原绑定及待机诊断。");
        if (ReadStopReason() is { } stop)
        {
            closeReason = stop; Wake();
            return GateResult.Unknown(stop, $"原安全待机要求停止（{stop}）；不会开始新观测，也不会滚动延长原截止。");
        }
        if (!request.CanHandOver && request.RecheckIdle is null)
            return GateResult.Unknown("NIGHT_STANDBY_RESPONSIBILITY_UNSETTLED",
                "上一目标仍有未结采集/运动/翻转责任，不能仅凭设备表面空闲交接。请检查原目标诊断和账本；不会自动清除或补发旧动作。");
        return null;
    }

    internal bool TryHandOver(bool real, string? configurationKey, Action commit, bool freshIdleConfirmed = false)
    {
        lock (sync)
        {
            if (HandoverRejection(real, configurationKey) is { } rejection)
            { snapshot = snapshot with { LastHandoverGate = rejection }; return false; }
            if (!(request.CanHandOver || (request.RecheckIdle is not null && freshIdleConfirmed))) return false;
            commit();
            snapshot = snapshot with { Phase = "Transferred", Message =
                (freshIdleConfirmed ? "本次手动启动已重新确认设备空闲；历史失败记录保留。" : string.Empty) +
                "安全责任已交接给手动启动的新序列；没有开关顶或自动续跑。" };
            Wake();
            return true;
        }
    }

    internal void StopForApplicationExit()
    {
        lock (sync)
        {
            if (!snapshot.IsActive) return;
            if (snapshot.Phase != "Closing")
                snapshot = snapshot with { Phase = "Shutdown", Message = "N.I.N.A. 已退出；软件安全待机不再运行，屋顶需由操作员或独立硬件保护。" };
            NativeCancellation.Request(shutdown);
            Wake();
        }
    }

    private void Wake() { if (wake.CurrentCount == 0) wake.Release(); }

    private string? ReadStopReason()
    {
        try { return request.StopGate()?.Code; }
        catch { return "NIGHT_SAFETY_READ_FAILED"; }
    }

    private async Task RunAsync()
    {
        try
        {
            changed(Snapshot);
            while (true)
            {
                lock (sync)
                {
                    if (snapshot.Phase != "Waiting") break;
                    closeReason ??= ReadStopReason();
                    if (closeReason is not null)
                    {
                        snapshot = snapshot with { Phase = "Closing", Reason = closeReason,
                            Message = $"独立安全收口：{closeReason}；正在核对停止与设备回读。" };
                        break;
                    }
                }
                await wake.WaitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
            }
            await PublishAsync().ConfigureAwait(false);
            if (Snapshot.Phase == "Closing")
            {
                var errors = new List<string>();
                try
                {
                    var close = Snapshot.Reason == WindowsRestartProbe.Code && request.RestartCloseout is not null
                        ? request.RestartCloseout : request.Closeout;
                    errors.AddRange(await close(shutdown.Token).ConfigureAwait(false));
                }
                catch (Exception ex) { errors.Add(ex.Message); }
                if (persistenceError is not null) errors.Add(persistenceError);
                lock (sync) snapshot = snapshot with
                {
                    Phase = errors.Count == 0 ? "Closed" : "CloseoutFailed", Errors = errors,
                    Message = errors.Count > 0 ? "独立安全收口未完成；仅监控报警，不自动重试，请人工处理：" + string.Join("；", errors) :
                        request.Real ? "独立安全收口已确认；未自动启动观测。" : "独立安全待机模拟结束；未操作硬件。",
                };
                await PublishAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            lock (sync) snapshot = snapshot with { Phase = "CloseoutFailed", Message = "安全待机异常，需人工处理：" + ex.Message, Errors = new[] { ex.Message } };
            await PublishAsync().ConfigureAwait(false);
        }
        finally
        {
            try
            {
                if (Snapshot.Phase is "MonitoringOnly" or "CloseoutFailed")
                {
                    completion.TrySetResult();
                    // Failure is not a release of observatory responsibility.
                    // Keep the original watch and lease; never retry a command
                    // or promote an unknown physical stop in this background loop.
                    while (!shutdown.IsCancellationRequested && (Snapshot.Phase is "MonitoringOnly" or "CloseoutFailed"))
                    {
                        bool update;
                        lock (sync)
                        {
                            var currentReason = ReadStopReason();
                            // Monitor-only is not permission to move, but an
                            // observed cutoff/unsafe must remain terminal even
                            // if a later poll happens to read Safe again.
                            closeReason ??= currentReason;
                            update = snapshot.LatestSafetyReason != currentReason ||
                                (snapshot.Reason is null && closeReason is not null);
                            snapshot = snapshot with { LatestSafetyReason = currentReason, Reason = snapshot.Reason ?? closeReason };
                        }
                        if (update) await PublishAsync().ConfigureAwait(false);
                        await wake.WaitAsync(TimeSpan.FromSeconds(1), shutdown.Token).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
            finally
            {
                if (Snapshot.Phase is "Transferred" or "Shutdown")
                    await PublishAsync().ConfigureAwait(false);
                subscription?.Dispose();
                reservation.Dispose(); // Transferred lease: never releases the new night.
                completion.TrySetResult();
            }
        }
    }

    private async Task PublishAsync()
    {
        try { await SaveAsync().ConfigureAwait(false); }
        catch (Exception ex)
        {
            persistenceError = "安全待机日志写入失败：" + ex.Message;
            lock (sync) snapshot = snapshot with
            {
                Message = snapshot.Message.Contains(persistenceError, StringComparison.Ordinal)
                    ? snapshot.Message : snapshot.Message + " " + persistenceError,
                Errors = (snapshot.Errors ?? Array.Empty<string>()).Append(persistenceError).Distinct().ToArray(),
            };
        }
        changed(Snapshot);
    }

    private async Task SaveAsync()
    {
        // Operator rechecks and the independent safety loop can publish together.
        // They must not race on the same atomic-replacement temporary file.
        await journalGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var current = Snapshot;
            var json = JsonSerializer.Serialize(new { schemaVersion = 1, updatedUtc = DateTimeOffset.UtcNow,
                parentJournal = request.ParentJournal, request.ConfigurationKey, request.CanHandOver,
                request.MonitoringOnly, request.InitialReason, current },
                new JsonSerializerOptions { WriteIndented = true });
            Directory.CreateDirectory(Path.GetDirectoryName(current.JournalPath)!);
            await File.WriteAllTextAsync(current.JournalPath + ".tmp", json).ConfigureAwait(false);
            File.Move(current.JournalPath + ".tmp", current.JournalPath, overwrite: true);
        }
        finally { journalGate.Release(); }
    }
}
