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
}

// The observing task has a finite lifetime. This owner has a separate lifetime,
// no acquisition entry point, and exactly one checked closeout attempt.
internal sealed record NightSafetyStandbyRequest(bool Real, string ConfigurationKey,
    DateTimeOffset? DeadlineUtc, string ParentJournal, bool CanHandOver,
    Func<Action, IDisposable?> WatchSafety, Func<GateResult?> StopGate,
    Func<CancellationToken, Task<IReadOnlyList<string>>> Closeout, string? Warning = null,
    Func<CancellationToken, Task<bool>>? RecheckIdle = null,
    bool MonitoringOnly = false, string? InitialReason = null);

internal sealed class NightSafetyStandby
{
    private readonly object sync = new();
    private readonly NightSafetyStandbyRequest request;
    private readonly IDisposable reservation;
    private readonly Action<NightSafetyStandbySnapshot> changed;
    private readonly SemaphoreSlim wake = new(0, 1);
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

    internal void RequestCloseout(string reason = "OPERATOR_CLOSEOUT")
    {
        lock (sync)
        {
            if (!snapshot.IsActive) return;
            closeReason ??= reason;
            Wake();
        }
    }

    // The caller holds the host lock. The same standby lock serializes transfer
    // against weather/deadline closeout. The new watch is armed by commit before
    // this watch or the cross-process lease is released; no unprotected gap.
    internal async Task<bool> RecheckIdleAsync(bool real, string? configurationKey, CancellationToken token)
    {
        lock (sync)
        {
            if (request.CanHandOver || request.RecheckIdle is null || snapshot.Phase is not ("Waiting" or "MonitoringOnly") ||
                closeReason is not null || request.Real != real ||
                request.ConfigurationKey != (configurationKey ?? string.Empty)) return false;
            if (ReadStopReason() is { } stop) { closeReason = stop; Wake(); return false; }
        }
        // Explicit new-night request only; no background recovery, device
        // connections, motion or cached promotion of a failed boundary.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try { return await request.RecheckIdle(timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { return false; }
    }

    internal bool TryHandOver(bool real, string? configurationKey, Action commit, bool freshIdleConfirmed = false)
    {
        lock (sync)
        {
            if (snapshot.Phase is not ("Waiting" or "MonitoringOnly") || closeReason is not null ||
                !(request.CanHandOver || (request.RecheckIdle is not null && freshIdleConfirmed)) ||
                request.Real != real || request.ConfigurationKey != (configurationKey ?? string.Empty)) return false;
            if (ReadStopReason() is { } stop) { closeReason = stop; Wake(); return false; }
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
            shutdown.Cancel();
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
                try { errors.AddRange(await request.Closeout(shutdown.Token).ConfigureAwait(false)); }
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
                        var currentReason = ReadStopReason();
                        bool update;
                        lock (sync)
                        {
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
        var current = Snapshot;
        var json = JsonSerializer.Serialize(new { schemaVersion = 1, updatedUtc = DateTimeOffset.UtcNow,
            parentJournal = request.ParentJournal, request.ConfigurationKey, request.CanHandOver,
            request.MonitoringOnly, request.InitialReason, current },
            new JsonSerializerOptions { WriteIndented = true });
        Directory.CreateDirectory(Path.GetDirectoryName(current.JournalPath)!);
        await File.WriteAllTextAsync(current.JournalPath + ".tmp", json).ConfigureAwait(false);
        File.Move(current.JournalPath + ".tmp", current.JournalPath, overwrite: true);
    }
}
