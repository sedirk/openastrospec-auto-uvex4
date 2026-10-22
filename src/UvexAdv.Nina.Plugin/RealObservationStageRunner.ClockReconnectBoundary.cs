using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

internal sealed partial class RealObservationStageRunner
{
    // The same ownership-boundary pattern as startup capture: retain the actual
    // owner task and latch an unconfirmed end; a fresh runner cannot erase it.
    private readonly MountClockReconnectBoundary mountClockReconnectBoundary = new();
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
