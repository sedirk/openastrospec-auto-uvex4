using NINA.Core.Enum;

namespace UvexAdv.Nina.Plugin;

internal sealed record NativeMeridianMountReadback(bool Connected, bool Slewing, bool Tracking,
    bool Parked, bool Home, PierSide Side)
{
    internal bool IdleTracking => Connected && !Slewing && Tracking && !Parked && !Home &&
        Side is PierSide.pierEast or PierSide.pierWest;
}

/// <summary>Single-dispatch mount operation. Injectable owner ports support hardware-free failure tests.</summary>
internal static class NativeMeridianMountOperation
{
    // ITelescopeMediator.SetTrackingEnabled returns the resulting TRACKING
    // STATE, not command success: false is the expected result when stopping.
    // A setter return alone (including false while disconnected) is never stop
    // proof. Keep the native settle interval and verify fresh owner readbacks.
    internal static async Task SetTrackingAndVerifyAsync(bool enabled, PierSide expectedSide,
        Func<bool, bool> setTracking, Func<NativeMeridianMountReadback> read,
        Action checkpoint, Func<CancellationToken, Task> settle, CancellationToken token)
    {
        void Verify(bool requireTrackingState)
        {
            token.ThrowIfCancellationRequested();
            checkpoint();
            var state = read();
            if (expectedSide is not (PierSide.pierEast or PierSide.pierWest) ||
                !state.Connected || state.Slewing || state.Parked || state.Home || state.Side != expectedSide ||
                (requireTrackingState && state.Tracking != enabled))
                throw new InvalidOperationException(enabled
                    ? "翻转前恢复跟踪/停止/镜筒侧读回未确认；没有下发翻转。"
                    : "停止跟踪/运动/镜筒侧读回未确认；没有下发翻转。");
        }
        Verify(requireTrackingState: false);
        _ = setTracking(enabled);
        await settle(token).ConfigureAwait(false);
        Verify(requireTrackingState: true);
    }

    internal static async Task<PierSide> VerifyObservedChangeAsync(PierSide before, PierSide expectedAfter,
        Func<NativeMeridianMountReadback> read, Action checkpoint,
        Func<CancellationToken, Task> settle, CancellationToken token)
    {
        void Verify()
        {
            token.ThrowIfCancellationRequested();
            checkpoint();
            var state = read();
            if (before is not (PierSide.pierEast or PierSide.pierWest) || expectedAfter == before ||
                !state.IdleTracking || state.Side != expectedAfter)
                throw new InvalidOperationException("转向后新侧别/停止/跟踪读回不稳定；不重发翻转或恢复旧向量。");
        }
        Verify();
        await settle(token).ConfigureAwait(false);
        Verify();
        return expectedAfter; // Orientation boundary, NOT proof of optical arrival.
    }

    internal static async Task<PierSide> ExecuteAsync(PierSide before,
        Func<NativeMeridianMountReadback> read,
        Func<CancellationToken, Task<bool>> flip,
        Action stopSlew, Action checkpoint,
        Func<CancellationToken, Task> settle, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        checkpoint();
        var initial = read();
        if (!initial.IdleTracking || initial.Side != before)
            throw new InvalidOperationException("翻转前镜筒侧或跟踪/空闲读回改变，未发送翻转。");
        using var motion = CancellationTokenSource.CreateLinkedTokenSource(token);
        motion.CancelAfter(TimeSpan.FromMinutes(5));
        Task<bool>? command = null;
        try
        {
            command = flip(motion.Token);
            while (!command.IsCompleted)
            {
                await Task.WhenAny(command, Task.Delay(250, motion.Token)).ConfigureAwait(false);
                motion.Token.ThrowIfCancellationRequested();
                checkpoint();
            }
            if (!await command.ConfigureAwait(false)) throw new InvalidOperationException("N.I.N.A. 未确认中天翻转成功。");
        }
        catch
        {
            NativeCancellation.Request(motion);
            try { stopSlew(); }
            finally
            {
                // Even a failed StopSlew cannot release an active owner task.
                if (command is not null)
                    try { await command.ConfigureAwait(false); } catch { }
            }
            throw;
        }
        await settle(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        checkpoint();
        var final = read();
        if (!final.IdleTracking || final.Side == before)
            throw new InvalidOperationException("翻转后没有新的镜筒侧、空闲与跟踪确认，禁止恢复曝光。");
        return final.Side;
    }
}
