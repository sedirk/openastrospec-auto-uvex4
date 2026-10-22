using UvexAdv.Observatory;
using UvexAdv.Phd2;

namespace UvexAdv.Nina.Plugin;

/// <summary>One native frame with checked cancellation, bounded stop and real-task join.</summary>
internal sealed class StartupGuideCaptureBoundary(TimeSpan? stopTimeout = null)
{
    private readonly TimeSpan timeout = stopTimeout ?? TimeSpan.FromSeconds(20);
    internal Task? UnjoinedOperation { get; private set; }
    internal string? StopIssue { get; private set; }

    internal async Task<Phd2SingleFrameResult> RunAsync(
        Func<CancellationToken, Task<Phd2SingleFrameResult>> capture,
        Func<CancellationToken, Task<Phd2StopCaptureResult>> stop,
        Func<CancellationToken, Task> checkSafety, CancellationToken token)
    {
        await checkSafety(token).ConfigureAwait(false);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task<Phd2SingleFrameResult> operation;
        try { operation = capture(cancellation.Token); }
        catch (Exception ex) { operation = Task.FromException<Phd2SingleFrameResult>(ex); }
        try
        {
            while (!operation.IsCompleted)
            {
                await Task.WhenAny(operation, Task.Delay(250, token)).ConfigureAwait(false);
                await checkSafety(token).ConfigureAwait(false);
            }
            return await operation.ConfigureAwait(false);
        }
        finally
        {
            cancellation.Cancel();
            using var stoppedWindow = new CancellationTokenSource(timeout);
            Task<Phd2StopCaptureResult> stopped;
            try { stopped = stop(stoppedWindow.Token); }
            catch (Exception ex) { stopped = Task.FromException<Phd2StopCaptureResult>(ex); }
            try
            {
                var result = await stopped.WaitAsync(timeout).ConfigureAwait(false);
                if (!result.ConfirmedIdle || result.FinalState != Phd2AppState.Stopped)
                    StopIssue = "PHD2 未证明停止。";
            }
            catch (Exception ex) { StopIssue = ex.Message; }
            try { await operation.WaitAsync(timeout).ConfigureAwait(false); }
            catch (Exception) { /* The original capture failure propagates, unless stop/join itself failed. */ }
            if (!operation.IsCompleted || !stopped.IsCompleted)
            {
                UnjoinedOperation = Task.WhenAll(operation, stopped);
                StopIssue = "原生单帧或停止任务未在有界清理内结束，不能释放所有者或重新证明空闲。";
            }
            if (StopIssue is { } issue)
                throw new PhysicalActionGateException(GateResult.Unknown("GUIDE_STARTUP_STOP_UNCONFIRMED", issue));
        }
    }
}
