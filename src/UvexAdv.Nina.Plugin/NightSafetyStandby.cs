using System.IO;
using System.Text.Json;
using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

public sealed record NightSafetyStandbySnapshot(string Id, string Phase, string Message,
    string JournalPath, DateTimeOffset? DeadlineUtc, bool PhysicalHardware,
    string? Reason = null, IReadOnlyList<string>? Errors = null)
{
    public bool IsActive => Phase is "Waiting" or "Closing";
}

// The observing task has a finite lifetime. This owner has a separate lifetime,
// no acquisition entry point, and exactly one checked closeout attempt.
internal sealed record NightSafetyStandbyRequest(bool Real, string ConfigurationKey,
    DateTimeOffset? DeadlineUtc, string ParentJournal, bool CanHandOver,
    Func<Action, IDisposable?> WatchSafety, Func<GateResult?> StopGate,
    Func<CancellationToken, Task<IReadOnlyList<string>>> Closeout, string? Warning = null,
    Func<CancellationToken, Task<bool>>? RecheckIdle = null);

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

    internal NightSafetyStandby(NightSafetyStandbyRequest request, IDisposable reservation,
        Action<NightSafetyStandbySnapshot> changed)
    {
        this.request = request; this.reservation = reservation; this.changed = changed;
        var id = $"standby-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}";
        var path = Path.Combine(Path.GetDirectoryName(request.ParentJournal)!, id + ".json");
        snapshot = new(id, "Waiting", "观测序列已退出；独立安全待机：AIWeather、晨光及原截止仍有效。" + request.Warning, path,
            request.DeadlineUtc, request.Real);
    }

    internal NightSafetyStandbySnapshot Snapshot { get { lock (sync) return snapshot; } }
    internal Task Completion { get; private set; } = Task.CompletedTask;

    // Called while the original night still owns the exclusive lease and watch.
    internal async Task ArmAsync()
    {
        subscription = request.WatchSafety(() => RequestCloseout("NIGHT_UNSAFE"));
        try { await SaveAsync().ConfigureAwait(false); }
        catch { subscription?.Dispose(); subscription = null; throw; }
    }

    internal void Start() => Completion = Task.Run(RunAsync);

    internal void RequestCloseout(string reason = "OPERATOR_CLOSEOUT")
    {
        lock (sync)
        {
            if (snapshot.Phase != "Waiting") return;
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
            if (request.CanHandOver || request.RecheckIdle is null || snapshot.Phase != "Waiting" ||
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
            if (snapshot.Phase != "Waiting" || closeReason is not null ||
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
            if (snapshot.Phase == "Waiting")
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
                    Message = errors.Count > 0 ? "独立安全收口未完成：" + string.Join("；", errors) :
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
            subscription?.Dispose();
            reservation.Dispose(); // A transferred lease is a no-op, never releases the new night.
        }
    }

    private async Task PublishAsync()
    {
        try { await SaveAsync().ConfigureAwait(false); }
        catch (Exception ex)
        {
            persistenceError = "安全待机日志写入失败：" + ex.Message;
            lock (sync) snapshot = snapshot with { Message = snapshot.Message + " " + persistenceError, Errors = new[] { persistenceError } };
        }
        changed(Snapshot);
    }

    private async Task SaveAsync()
    {
        var current = Snapshot;
        var json = JsonSerializer.Serialize(new { schemaVersion = 1, updatedUtc = DateTimeOffset.UtcNow,
            parentJournal = request.ParentJournal, request.ConfigurationKey, request.CanHandOver, current },
            new JsonSerializerOptions { WriteIndented = true });
        Directory.CreateDirectory(Path.GetDirectoryName(current.JournalPath)!);
        await File.WriteAllTextAsync(current.JournalPath + ".tmp", json).ConfigureAwait(false);
        File.Move(current.JournalPath + ".tmp", current.JournalPath, overwrite: true);
    }
}
