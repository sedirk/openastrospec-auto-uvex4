using UvexAdv.Observatory;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Equipment.MyDome;
using NINA.Equipment.Equipment.MyFlatDevice;

namespace UvexAdv.Nina.Plugin;

internal sealed partial class RealObservationStageRunner
{
    // The same ownership-boundary pattern as startup capture: retain the actual
    // owner task and latch an unconfirmed end; a fresh runner cannot erase it.
    private readonly MountClockReconnectBoundary mountClockReconnectBoundary = new();
}

// A startup clock repair does not expose the optical path or move the mount.
// This boundary accepts only known, unchanged endpoint states. It is NOT an
// alternative action gate and is never used by an in-target clock recovery.
internal sealed class StartupClockRecoveryBoundary(
    Func<bool, GateResult> checkLocal,
    Func<CancellationToken, Task<GateResult>> readStoppedGuide,
    Func<CancellationToken, Task<GateResult>> readPhotometryIdle)
{
    internal static bool CanEnter(ObservationStage stage, bool terminalRecovery, bool weakSupervision,
        bool hasEarlierTargetState) => stage == ObservationStage.ValidateNightSetup &&
        !terminalRecovery && !weakSupervision && !hasEarlierTargetState;

    internal static GateResult OpticalPath(DomeInfo roof, FlatDeviceInfo cover,
        NinaEnvironmentDeviceSelection expected, bool roofLifecycleCommitted,
        ShutterState? originalRoof = null, CoverState? originalCover = null)
    {
        if (!roof.Connected || roof.DeviceId != expected.DomeOrRoofId || !expected.HasDomeOrRoof ||
            !cover.Connected || cover.DeviceId != expected.OpticalCoverId || !expected.HasOpticalCover ||
            !cover.SupportsOpenClose)
            return GateResult.Unknown("MOUNT_CLOCK_STARTUP_OPTICAL_PATH_UNCONFIRMED",
                "启动校时需要已绑定屋顶及镜盖的新鲜连接与身份；不会自动接管未知设备。");
        if (roof.Slewing || roof.ShutterStatus is not (ShutterState.ShutterOpen or ShutterState.ShutterClosed) ||
            cover.CoverState is not (CoverState.Open or CoverState.Closed))
            return GateResult.Unknown("MOUNT_CLOCK_STARTUP_OPTICAL_PATH_MOVING_OR_UNKNOWN",
                "启动校时只接受静止的开/关端点；屋顶或镜盖正在运动、错误或未知时不能重连。");
        if (roofLifecycleCommitted && roof.ShutterStatus == ShutterState.ShutterClosed)
            return GateResult.Fail("ROOF_CLOSED_AFTER_OPEN", "本轮屋顶已经打开或请求打开后又关闭；启动校时不能重新授予开顶权限。");
        if (originalRoof is { } oldRoof && oldRoof != roof.ShutterStatus ||
            originalCover is { } oldCover && oldCover != cover.CoverState)
            return GateResult.Unknown("MOUNT_CLOCK_STARTUP_OPTICAL_PATH_CHANGED",
                "校时期间屋顶或镜盖状态改变；不借用启动例外继续或重新打开。");
        return GateResult.Pass("MOUNT_CLOCK_STARTUP_OPTICAL_PATH_STATIONARY",
            "已知静止的顶盖端点允许无运动校时；开顶和开盖仍须随后分别通过原安全链。");
    }

    internal async Task<GateResult> CheckAsync(bool requireConnectedMount, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var local = checkLocal(requireConnectedMount);
        if (local.Disposition != GateDisposition.Passed) return local;
        // Only read-only RPCs are delegated here; bound their wait independently
        // of native lifecycle tasks, whose actual tasks remain retained below.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var guide = await readStoppedGuide(timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            if (guide.Disposition != GateDisposition.Passed) return guide;
            var photometry = await readPhotometryIdle(timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            if (photometry.Disposition != GateDisposition.Passed) return photometry;
            guide = await readStoppedGuide(timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            if (guide.Disposition != GateDisposition.Passed) return guide;
            token.ThrowIfCancellationRequested();
            return checkLocal(requireConnectedMount); // Late Unsafe, capture or motion wins.
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return GateResult.Unknown("MOUNT_CLOCK_STARTUP_OWNER_READ_TIMEOUT",
                "启动校时的只读所有者停止核验超时；未确认的读取不是空闲证明。");
        }
    }
}

// N.I.N.A.'s connect/disconnect methods do not accept cancellation. Cancelling
// WaitAsync cancels only our wait, not the native owner operation. No second
// connection, stop command, ASCOM instance or background retry is created here.
internal sealed class MountClockReconnectBoundary(TimeSpan? joinTimeout = null)
{
    private readonly object sync = new();
    private readonly TimeSpan joinWindow = joinTimeout ?? TimeSpan.FromSeconds(3);
    private Task? operation;
    private string operationName = "";
    private string? issue;

    internal Task? PendingOperation { get { lock (sync) return operation; } }
    internal string? Issue { get { lock (sync) return issue; } }
    internal bool HasUnjoinedOperation { get { lock (sync) return operation is { IsCompleted: false }; } }
    internal GateResult? BoundaryGate
    {
        get
        {
            lock (sync)
                return issue is not null || operation is { IsCompleted: false }
                    ? GateResult.Unknown("MOUNT_CLOCK_OWNER_OPERATION_UNCONFIRMED", issue ??
                        $"N.I.N.A. telescope {operationName} is still running; owner stop cannot yet be attested.")
                    : null;
        }
    }

    internal async Task RunAsync(string name, Func<Task> start, TimeSpan timeout, CancellationToken token)
    {
        Task actual;
        lock (sync)
        {
            token.ThrowIfCancellationRequested();
            if (BoundaryGate is { } pending) throw new PhysicalActionGateException(pending);
            operationName = name;
            actual = start();
            operation = actual;
        }
        try
        {
            await actual.WaitAsync(timeout, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
        }
        finally
        {
            if (!actual.IsCompleted)
            {
                // Bounded cleanup is independent of the cancelled observation
                // token. It only joins the original task; it sends no command.
                try { await actual.WaitAsync(joinWindow).ConfigureAwait(false); }
                catch (Exception) { /* Preserve the original timeout/cancel/fault. */ }
                lock (sync)
                {
                    if (!actual.IsCompleted)
                        issue ??= $"N.I.N.A. telescope {name} did not finish within the bounded owner join; " +
                            "no subsequent target, reconnect, mechanical cleanup or fresh idle substitution is authorized.";
                }
            }
        }
    }

    internal async Task<T> RunAsync<T>(string name, Func<Task<T>> start, TimeSpan timeout, CancellationToken token)
    {
        Task<T>? actual = null;
        await RunAsync(name, (Func<Task>)(() => actual = start()), timeout, token).ConfigureAwait(false);
        return await actual!.ConfigureAwait(false);
    }
}
