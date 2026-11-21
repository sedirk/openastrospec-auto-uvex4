using System.Diagnostics;
using System.Runtime.CompilerServices;
using NINA.Equipment.Equipment.MyDome;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Model;
using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

// IDomeMediator.Connect has no cancellation parameter. Stop waiting promptly,
// but keep its native operation visible to *every* runner using this mediator.
// Neither timeout nor caller cancellation is evidence that N.I.N.A. has joined.
internal sealed class NinaRoofConnectionBoundary
{
    private static readonly ConditionalWeakTable<IDomeMediator, NinaRoofConnectionBoundary> Owners = new();
    internal static NinaRoofConnectionBoundary For(IDomeMediator mediator) => Owners.GetValue(mediator, static _ => new());
    private readonly object sync = new();
    private Task<GateResult>? nativeConnect;
    private static GateResult StillConnecting() => GateResult.Unknown("ROOF_ADAPTER_CONNECT_PENDING",
        "N.I.N.A. 原屋顶连接调用尚未退出；不能重试连接、开关顶或继续观测。请查看设备页、原串口及供电；不要并行打开 RRCI。");

    internal GateResult? PendingGate
    {
        get
        {
            lock (sync) return nativeConnect is { IsCompleted: false } ? StillConnecting() : null;
        }
    }

    internal async Task<GateResult> ConnectAsync(Func<Task<bool>> connect, Func<GateResult> validateBinding,
        TimeSpan limit, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
        wait.CancelAfter(limit);
        var dispatch = wait.Token;
        Task<GateResult> operation;
        lock (sync)
        {
            if (nativeConnect is { IsCompleted: false }) return StillConnecting();
            nativeConnect = operation = Task.Run(async () =>
            {
                try
                {
                    dispatch.ThrowIfCancellationRequested();
                    var selected = validateBinding();
                    if (selected.Disposition != GateDisposition.Passed) return selected;
                    dispatch.ThrowIfCancellationRequested();
                    return await connect().ConfigureAwait(false)
                        ? GateResult.Pass("ROOF_ADAPTER_CONNECT_ACK", "N.I.N.A. 屋顶连接已返回；尚未确认屋顶位置。")
                        : GateResult.Unknown("ROOF_ADAPTER_CONNECT_FAILED", "N.I.N.A. 未能连接原绑定屋顶；请查看设备页和原串口日志。");
                }
                catch (Exception ex)
                {
                    return GateResult.Unknown("ROOF_ADAPTER_CONNECT_FAILED", $"N.I.N.A. 原屋顶连接异常：{ex.GetType().Name} · {ex.Message}");
                }
            });
        }
        try { return await operation.WaitAsync(dispatch).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return GateResult.Unknown("ROOF_ADAPTER_CONNECT_TIMEOUT",
                $"等候 N.I.N.A. 原屋顶连接 {limit.TotalSeconds:G} 秒超时；旧调用若仍未退出，会保留阻断。没有自动重连或发出机械动作。");
        }
    }
}

internal enum BoundRoofPosition { Closed, Open, Opening, Closing, ReadbackError, Unconfirmed }

internal static class NinaRoofReadback
{
    internal static BoundRoofPosition Position(DomeInfo? roof, string expectedId)
    {
        if (roof is not { Connected: true } || !Matches(roof, expectedId)) return BoundRoofPosition.Unconfirmed;
        return roof.ShutterStatus switch
        {
            ShutterState.ShutterClosed when !roof.Slewing => BoundRoofPosition.Closed,
            ShutterState.ShutterOpen when !roof.Slewing => BoundRoofPosition.Open,
            ShutterState.ShutterOpening => BoundRoofPosition.Opening,
            ShutterState.ShutterClosing => BoundRoofPosition.Closing,
            ShutterState.ShutterError => BoundRoofPosition.ReadbackError,
            _ => BoundRoofPosition.Unconfirmed
        };
    }

    private static bool Matches(DomeInfo roof, string expectedId) =>
        !string.IsNullOrWhiteSpace(expectedId) && string.Equals(roof.DeviceId, expectedId, StringComparison.Ordinal);

    internal static GateResult CheckObserving(DomeInfo roof, string expectedId, bool allowClosed)
    {
        if (!roof.Connected)
            return GateResult.Unknown("ROOF_STATE_UNKNOWN", "原绑定屋顶接口未连接，位置未知；请查看 RRCI 连接及原串口。");
        if (!Matches(roof, expectedId))
            return GateResult.Fail("ROOF_ADAPTER_IDENTITY_MISMATCH",
                $"已连接屋顶 '{roof.DeviceId}' 不是原绑定 '{expectedId}'；不会接管另一屋顶。");
        var position = Position(roof, expectedId);
        if (position == BoundRoofPosition.Open || allowClosed && position == BoundRoofPosition.Closed)
            return GateResult.Pass("ROOF_ENDPOINT_CONFIRMED", $"原绑定屋顶已停止，状态 {roof.ShutterStatus}。");
        if (position == BoundRoofPosition.Closed)
            return GateResult.Fail("ROOF_NOT_OPEN", "原绑定屋顶已读回关闭，未授权观测。");
        return GateResult.Unknown("ROOF_POSITION_UNCONFIRMED",
            $"屋顶接口已连接，但未确认静止位置（{roof.ShutterStatus}, Slewing={roof.Slewing}）；Error/Unknown 不表示已开或已关。");
    }

    internal static async Task<GateResult> WaitForEndpointAsync(Func<DomeInfo> read, Func<GateResult?> inhibit,
        string expectedId, ShutterState endpoint, TimeSpan limit, TimeSpan poll, CancellationToken token)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (inhibit() is { } blocked) return blocked;
            var roof = read();
            if (!roof.Connected)
                return GateResult.Unknown("ROOF_DISCONNECTED_DURING_TRANSITION", "屋顶过渡中原绑定接口断连，未确认终态。");
            if (!Matches(roof, expectedId))
                return GateResult.Fail("ROOF_IDENTITY_CHANGED_DURING_TRANSITION", "屋顶过渡中设备身份改变，未确认终态。");
            var remaining = limit - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero)
                return GateResult.Unknown("ROOF_TRANSITION_TIMEOUT",
                    $"未在 {limit.TotalSeconds:G} 秒内读回 {endpoint} 且静止；最后为 {roof.ShutterStatus}, Slewing={roof.Slewing}。");
            if (roof.ShutterStatus == endpoint && !roof.Slewing)
                return GateResult.Pass(endpoint == ShutterState.ShutterOpen ? "UNATTENDED_ROOF_OPEN" : "UNATTENDED_ROOF_CLOSED",
                    $"原绑定屋顶读回 {endpoint} 且已停止。");
            if (roof.ShutterStatus == ShutterState.ShutterError)
                return GateResult.Unknown("ROOF_TRANSITION_ERROR", "屋顶过渡中返回 Error，不能证明到位；不会自动重新发令。");
            await Task.Delay(remaining < poll ? remaining : poll, token).ConfigureAwait(false);
        }
    }
}

// Called only under the shared runner's mechanical gate, after original
// mount/guider/clearance checks; observation of an existing close consumes the
// same one-shot allowance as dispatch. Never turn a failed close into a retry.
internal sealed class NinaRoofCloseBoundary
{
    private bool closeObservedOrDispatched;

    internal async Task<GateResult> CloseOnceAsync(Func<DomeInfo> read, Func<GateResult?> inhibit,
        string expectedId, Func<CancellationToken, Task<bool>> close, Func<CancellationToken, Task<GateResult>> waitClosed,
        CancellationToken token, bool observeOnly = false)
    {
        token.ThrowIfCancellationRequested();
        if (inhibit() is { } blocked) return blocked;
        var roof = read();
        if (observeOnly) closeObservedOrDispatched = true;
        switch (NinaRoofReadback.Position(roof, expectedId))
        {
            case BoundRoofPosition.Closed:
                return GateResult.Pass("UNATTENDED_ROOF_CLOSED", "原绑定屋顶已读回关闭且静止，未重新发令。");
            case BoundRoofPosition.Closing:
                closeObservedOrDispatched = true;
                return await waitClosed(token).ConfigureAwait(false);
            case BoundRoofPosition.Open when roof.CanSetShutter && !closeObservedOrDispatched && !observeOnly:
                token.ThrowIfCancellationRequested();
                closeObservedOrDispatched = true;
                if (!await close(token).ConfigureAwait(false))
                    return GateResult.Unknown("ROOF_CLOSE_COMMAND_REJECTED", "N.I.N.A./RRCI 未确认接受关顶；未重试，控制器联锁不绕过。");
                return await waitClosed(token).ConfigureAwait(false);
            default:
                return GateResult.Unknown("ROOF_CLOSE_NOT_AUTHORIZED",
                    $"未授权关顶或已有一次未确认的关顶动作（{roof.ShutterStatus}, Connected={roof.Connected}, Slewing={roof.Slewing}）；未发新指令，请现场核对。");
        }
    }
}
