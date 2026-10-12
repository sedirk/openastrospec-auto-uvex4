using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

/// <summary>
/// Cancels only the current joined stage, not the logical target or night.
/// Existing owner cancellation paths stop/retain debt; only this boundary may
/// translate that internal interruption into a meridian segment handoff.
/// </summary>
internal sealed class NativeMeridianStageBoundary(CancellationToken parent) : IDisposable
{
    private readonly CancellationTokenSource stage = CancellationTokenSource.CreateLinkedTokenSource(parent);
    internal bool Interrupted { get; private set; }
    internal void Interrupt()
    {
        parent.ThrowIfCancellationRequested();
        Interrupted = true;
        stage.Cancel();
        stage.Token.ThrowIfCancellationRequested();
    }

    internal async Task<StageResult> ExecuteAsync(Func<CancellationToken, Task<StageResult>> execute,
        Func<StageResult> handoff)
    {
        try
        {
            var result = await execute(stage.Token).ConfigureAwait(false);
            // An inner adapter may turn cancellation into a result. Do not let
            // it resume recovery/motion or complete a later acquisition stage.
            stage.Token.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when (Interrupted && !parent.IsCancellationRequested)
        {
            // Exception filters run before inner finally blocks unwind. The
            // operator/safety token may have been cancelled during owner join.
            parent.ThrowIfCancellationRequested();
            return handoff();
        }
    }

    public void Dispose() => stage.Dispose();
}
