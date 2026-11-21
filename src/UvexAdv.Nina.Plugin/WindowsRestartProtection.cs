using System.Reflection;
using System.Runtime.InteropServices;
using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

internal enum UpdateRestartState { Unknown, NotRequired, Required }

// Read-only Windows Update Agent. No update searches, installation, deadlines,
// reboots, registry guesses, or device access. A stuck COM server must not hang
// shutdown or create an unbounded queue of reader threads.
internal sealed class WindowsRestartProbe(Func<UpdateRestartState> query,
    TimeSpan interval, TimeSpan readTimeout)
{
    internal const string Code = "WINDOWS_UPDATE_RESTART_REQUIRED";
    internal const string Message = "Windows 更新明确要求重启；不再开始观测或开顶，已开顶按原安全链提前收口。";
    internal static WindowsRestartProbe System { get; } = new(ReadWindows, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2));
    internal TimeSpan Interval => interval;
    private readonly object sync = new();
    private Task<UpdateRestartState>? inFlight;
    private long nextPoll;
    private UpdateRestartState last;
    private UpdateRestartState? lastReported;

    private static UpdateRestartState ReadWindows()
    {
        if (!OperatingSystem.IsWindows()) return UpdateRestartState.Unknown;
        var type = Type.GetTypeFromProgID("Microsoft.Update.SystemInfo", throwOnError: true)!;
        var info = Activator.CreateInstance(type)!;
        try
        {
            return type.InvokeMember("RebootRequired", BindingFlags.GetProperty, null, info, null) switch
            { true => UpdateRestartState.Required, false => UpdateRestartState.NotRequired, _ => UpdateRestartState.Unknown };
        }
        finally { Marshal.FinalReleaseComObject(info); }
    }

    private Task<UpdateRestartState> Start(bool fresh)
    {
        lock (sync)
        {
            if (inFlight is { IsCompleted: false }) return inFlight;
            if (!fresh && Environment.TickCount64 < nextPoll) return Task.FromResult(last);
            return inFlight = Task.Run(() =>
            {
                UpdateRestartState result;
                try { result = query(); }
                catch (Exception) { result = UpdateRestartState.Unknown; }
                lock (sync)
                {
                    // An unreadable interface cannot retract an earlier positive.
                    if (result != UpdateRestartState.Unknown || last != UpdateRestartState.Required) last = result;
                    nextPoll = Environment.TickCount64 + (long)interval.TotalMilliseconds;
                    if (lastReported != result)
                    {
                        lastReported = result;
                        try
                        {
                            if (result == UpdateRestartState.Unknown) NINA.Core.Utility.Logger.Warning("WINDOWS_UPDATE_STATUS_UNKNOWN: 无法读取 Windows 更新重启状态；不是待重启证明。");
                            else if (result == UpdateRestartState.Required) NINA.Core.Utility.Logger.Warning(Code + ": " + Message);
                        }
                        catch (Exception ex) { global::System.Diagnostics.Trace.TraceWarning("WUA diagnostic: {0}", ex.Message); }
                    }
                    return last;
                }
            });
        }
    }

    internal async Task<UpdateRestartState> ReadAsync(CancellationToken token, bool fresh = false)
    {
        token.ThrowIfCancellationRequested();
        try { return await Start(fresh).WaitAsync(readTimeout, token).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            lock (sync) return last == UpdateRestartState.Required ? last : UpdateRestartState.Unknown;
        }
    }

    // Called from the existing standby poll/handover lock: never block it on COM.
    internal GateResult? PollGate()
    {
        _ = Start(false);
        lock (sync) return last == UpdateRestartState.Required ? GateResult.Fail(Code, Message) : null;
    }

    internal async Task<GateResult?> BeforeOpeningAsync(CancellationToken token) =>
        (await ReadAsync(token, fresh: true).ConfigureAwait(false)) switch
        {
            UpdateRestartState.Required => GateResult.Fail(Code, Message),
            UpdateRestartState.Unknown => GateResult.Unknown("WINDOWS_UPDATE_STATUS_UNKNOWN",
                "开顶前无法核验 Windows 更新重启状态，本次未授权新开顶；不把读取失败当作关顶命令。"),
            _ => null
        };
}

// One watch per owner, not per exposure. Stop/join before releasing the lease;
// a callback can never cancel a later run. Positive evidence is latched even
// if WUA later changes; cancellation only stops work, it is not physical proof.
internal sealed class WindowsRestartWatch : IAsyncDisposable
{
    private readonly CancellationTokenSource stop = new();
    private readonly Task loop;
    private int required;
    internal bool Required => Volatile.Read(ref required) != 0;
    private WindowsRestartWatch(WindowsRestartProbe probe, UpdateRestartState initial, Action trip)
    {
        void Trip()
        {
            if (Interlocked.Exchange(ref required, 1) == 0) trip();
        }
        if (initial == UpdateRestartState.Required) { Trip(); loop = Task.CompletedTask; return; }
        loop = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    await Task.Delay(probe.Interval, stop.Token).ConfigureAwait(false);
                    if (await probe.ReadAsync(stop.Token).ConfigureAwait(false) != UpdateRestartState.Required) continue;
                    Trip(); return;
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        });
    }

    internal static async Task<WindowsRestartWatch> StartAsync(WindowsRestartProbe probe, Action trip, CancellationToken token) =>
        new(probe, await probe.ReadAsync(token, fresh: true).ConfigureAwait(false), trip);
    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        try { await loop.ConfigureAwait(false); }
        finally { stop.Dispose(); }
    }
}

internal static class WindowsRestartCloseout
{
    // Called only by the existing reservation owner after observing has joined.
    internal static async Task<IReadOnlyList<string>> RunAsync(Func<BoundRoofPosition> read, bool weak,
        Func<CancellationToken, Task<IReadOnlyList<string>>> checkedCloseout, CancellationToken token,
        TimeSpan? transitionBudget = null, TimeSpan? poll = null)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            var roof = read();
            // An in-progress close belongs to its original owner: wait for the
            // readback, never send a second close. A lost identity is not a lagging poll.
            var sawClosing = roof == BoundRoofPosition.Closing;
            var budget = transitionBudget ?? TimeSpan.Zero;
            if (roof == BoundRoofPosition.ReadbackError && budget > TimeSpan.FromSeconds(3)) budget = TimeSpan.FromSeconds(3);
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            while (roof is BoundRoofPosition.Opening or BoundRoofPosition.Closing or BoundRoofPosition.ReadbackError && elapsed.Elapsed < budget)
            {
                await Task.Delay(poll ?? TimeSpan.FromMilliseconds(200), token).ConfigureAwait(false);
                roof = read(); sawClosing |= roof == BoundRoofPosition.Closing;
            }
            if (roof == BoundRoofPosition.Closed) return [];
            if (sawClosing) return ["正在执行的关顶未读回关闭，未重发指令，请立即现场核对。"];
            if (roof != BoundRoofPosition.Open || weak)
                return [weak ? "Windows 待重启；屋顶未确认关闭。有人弱监督无自动屋顶权限，请现场收口。" :
                    "Windows 待重启；原绑定屋顶未返回稳定开/关状态，未发机械指令，未确认关顶，请立即现场核对。"];
            var errors = (await checkedCloseout(token).ConfigureAwait(false)).ToList();
            if (read() != BoundRoofPosition.Closed) errors.Add("未收到原绑定屋顶已关闭的终态回读，请立即现场处理；不重发关顶。");
            else if (errors.Count != 0) errors.Add("原绑定屋顶已读回关闭；上述其他收口问题仍未解决，不重开屋顶。");
            return errors;
        }
        catch (Exception ex) { return ["Windows 待重启，收口未确认，请立即现场处理：" + ex.Message]; }
    }
}

internal sealed record WindowsRestartRunPort(WindowsRestartProbe Probe,
    Func<bool, CancellationToken, Task<IReadOnlyList<string>>> Close)
{
    internal async Task ExecuteAsync(Func<CancellationToken, Task> observe, Action<string, bool> notify, CancellationToken token)
    {
        using var work = CancellationTokenSource.CreateLinkedTokenSource(token);
        var ran = false;
        Exception? observationFailure = null;
        var watch = await WindowsRestartWatch.StartAsync(Probe, () => NativeCancellation.Request(work), token).ConfigureAwait(false);
        try
        {
            if (!watch.Required) { ran = true; await observe(work.Token).ConfigureAwait(false); }
        }
        catch (Exception ex) { observationFailure = ex; }
        finally { await watch.DisposeAsync().ConfigureAwait(false); }
        if (watch.Required || Probe.PollGate() is not null)
        {
            // The original observer has joined; do not reuse its cancelled token for safing.
            IReadOnlyList<string> errors;
            try { errors = await Close(ran, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { errors = ["未确认收口：" + ex.Message]; }
            var message = WindowsRestartProbe.Message + (errors.Count == 0 ? " 原绑定屋顶已读回关闭；未续拍。" :
                " 收口未确认，请立即现场处理：" + string.Join("；", errors));
            notify(message, errors.Count != 0);
            throw new InvalidOperationException(WindowsRestartProbe.Code + ": " + message, observationFailure);
        }
        if (observationFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(observationFailure).Throw();
    }
}
internal interface IWindowsRestartRun
{
    WindowsRestartRunPort? RestartPort { get; }
}
