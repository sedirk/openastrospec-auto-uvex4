using System.Diagnostics;
using UvexAdv.Observatory;
using UvexAdv.Phd2;
using NINA.Equipment.Interfaces.Mediator;

namespace UvexAdv.Nina.Plugin;

// PHD2 Stopped acknowledges capture, not completion of an ASCOM pulse or the
// next NINA mount poll. This read-only handoff never grants motion authority.
internal static class Phd2MountIdleWait
{
    internal static readonly TimeSpan MaximumWait = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(3);
    internal const string TimeoutCode = "PHD2_STOP_MOUNT_IDLE_TIMEOUT";

    // Target completion needs the same pulse-drain window as calibration
    // handoff. StopCapture's RPC receipt precedes NINA's next device poll.
    // This overload has read-only interfaces: it cannot stop/slew/reconnect.
    internal static Task<GateResult> WaitAfterTargetStopAsync(IPhd2Client phd2,
        ITelescopeMediator mount, string expectedMountId, CancellationToken token,
        Func<TimeSpan>? elapsed = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        var stopped = phd2.Snapshot;
        return WaitAsync(async ct =>
        {
            var state = await phd2.GetAppStateAsync(ct).ConfigureAwait(false);
            var current = phd2.Snapshot;
            if (!stopped.IsConnected || !current.IsConnected ||
                stopped.AppState != Phd2AppState.Stopped || state != Phd2AppState.Stopped ||
                current.ConnectionEpoch != stopped.ConnectionEpoch || current.GuideEpoch != stopped.GuideEpoch ||
                current.PendingSettleOperationId is not null || current.Phd2Paused || current.AutomationPaused)
                return GateResult.Unknown("NATIVE_TARGET_GUIDER_STOP_CHANGED",
                    "目标结束等待期间导星停止状态或所有者改变；未发出新的采集或机械动作。");
            var info = mount.GetInfo();
            if (!info.Connected || string.IsNullOrWhiteSpace(expectedMountId) ||
                !string.Equals(info.DeviceId, expectedMountId, StringComparison.Ordinal) || info.Slewing)
                return GateResult.Unknown("NATIVE_TARGET_MOUNT_STOP_CHANGED",
                    "目标结束等待期间赤道仪连接、身份或停止状态不符。");
            return info.IsPulseGuiding
                ? GateResult.Unknown("G3_SEARCH_PULSE_GUIDING_ACTIVE", "等待末次导星脉冲及 N.I.N.A. 轮询更新。")
                : GateResult.Pass("NATIVE_TARGET_MOUNT_IDLE_SAMPLE", "同一赤道仪已报告无转向、无导星脉冲。");
        }, token, elapsed, delay);
    }

    internal static async Task<GateResult> WaitAsync(
        Func<CancellationToken, Task<GateResult>> probe,
        CancellationToken cancellationToken,
        Func<TimeSpan>? elapsed = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        var watch = Stopwatch.StartNew();
        elapsed ??= () => watch.Elapsed;
        delay ??= Task.Delay;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(MaximumWait);
        TimeSpan? quietSince = null;
        var samples = 0;
        var pulseSamples = 0;
        GateResult Timeout() => GateResult.Unknown(TimeoutCode,
            "PHD2 capture stopped, but mount pulse completion and a continuous idle readback window were not confirmed within the bounded wait; no new capture or motion was issued.",
            new Dictionary<string, double> { ["elapsedSeconds"] = elapsed().TotalSeconds,
                ["mountIdleSamples"] = samples, ["pulseActiveSamples"] = pulseSamples,
                ["maximumWaitSeconds"] = MaximumWait.TotalSeconds });
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (elapsed() >= MaximumWait) return Timeout();
                var gate = await probe(deadline.Token).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                samples++;
                if (elapsed() >= MaximumWait) return Timeout();
                if (gate.Disposition == GateDisposition.Passed)
                {
                    quietSince ??= elapsed();
                    if (elapsed() - quietSince.Value >= QuietPeriod)
                        return GateResult.Pass("PHD2_STOP_MOUNT_IDLE_CONFIRMED",
                            "The same stopped PHD2 session and mount idle readbacks remained valid for the handoff window; fresh G3 evidence and all original motion gates are still required.",
                            new Dictionary<string, double> { ["elapsedSeconds"] = elapsed().TotalSeconds,
                                ["mountIdleSamples"] = samples, ["pulseActiveSamples"] = pulseSamples });
                }
                else if (gate.Code == "G3_SEARCH_PULSE_GUIDING_ACTIVE")
                {
                    pulseSamples++;
                    quietSince = null;
                }
                else return gate; // Disconnect, resumed guiding, pier change, slew, etc. are not retried.
                await delay(TimeSpan.FromMilliseconds(500), deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            return Timeout();
        }
    }
}
