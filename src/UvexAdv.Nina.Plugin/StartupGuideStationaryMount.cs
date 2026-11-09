using System.Diagnostics;
using NINA.Equipment.Equipment.MyTelescope;
using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

/// <summary>Read-only startup retirement proof, never a field/target motion transform.</summary>
internal sealed record StartupGuideMountReadback(string DeviceId, bool Connected, bool Tracking,
    bool Slewing, bool PulseGuiding, string PierSide, string Epoch, double RaDegrees, double DecDegrees,
    double AltitudeDegrees, double AzimuthDegrees, DateTimeOffset DeviceUtc, DateTimeOffset ObservedUtc)
{
    internal static StartupGuideMountReadback Read(TelescopeInfo info, DateTimeOffset now) => new(
        info.DeviceId, info.Connected, info.TrackingEnabled, info.Slewing, info.IsPulseGuiding,
        info.SideOfPier.ToString(), info.Coordinates?.Epoch.ToString() ?? "", info.RightAscension * 15,
        info.Declination, info.Altitude, info.Azimuth,
        new DateTimeOffset(DateTime.SpecifyKind(info.UTCDate, DateTimeKind.Utc)), now);

    internal G3FrameMountReadback SkyReadback => new(RaDegrees, DecDegrees, Epoch, PierSide, ObservedUtc);
}

internal static class StartupGuideStationaryMount
{
    internal static readonly TimeSpan MaximumReadbackAge = TimeSpan.FromSeconds(10);

    internal static GateResult ValidateReadback(StartupGuideMountReadback sample, string expectedMountId)
    {
        if (!sample.Connected || string.IsNullOrWhiteSpace(expectedMountId) || sample.DeviceId != expectedMountId ||
            sample.Slewing || sample.PulseGuiding || sample.PierSide is not ("pierEast" or "pierWest") ||
            string.IsNullOrWhiteSpace(sample.Epoch))
            return Rejected("赤道仪所有者、镜侧、坐标历元或无转向/脉冲状态未确认。");
        if (sample.DeviceUtc == default || sample.ObservedUtc == default)
            return Rejected("赤道仪只读轮询时钟不可用，不能确认状态更新。");
        if (!double.IsFinite(sample.RaDegrees) || sample.RaDegrees is < 0 or >= 360 ||
            !double.IsFinite(sample.DecDegrees) || Math.Abs(sample.DecDegrees) > 90 ||
            !sample.Tracking && (!double.IsFinite(sample.AltitudeDegrees) || Math.Abs(sample.AltitudeDegrees) > 90 ||
                !double.IsFinite(sample.AzimuthDegrees) || sample.AzimuthDegrees is < 0 or >= 360))
            return Rejected("当前跟踪模式所需的实测赤道/地平坐标不可用。");
        return GateResult.Pass("GUIDE_STARTUP_MOUNT_READBACK_FRESH", "同一所有者的新鲜只读赤道仪状态。");
    }

    internal static GateResult Validate(StartupGuideMountReadback before, StartupGuideMountReadback current,
        string expectedMountId, double toleranceArcseconds)
    {
        var initial = ValidateReadback(before, expectedMountId);
        if (initial.Disposition != GateDisposition.Passed) return initial;
        var fresh = ValidateReadback(current, expectedMountId);
        if (fresh.Disposition != GateDisposition.Passed) return fresh;
        if (before.Tracking != current.Tracking || before.PierSide != current.PierSide || before.Epoch != current.Epoch ||
            current.DeviceUtc < before.DeviceUtc || current.ObservedUtc < before.ObservedUtc ||
            Math.Abs(((current.ObservedUtc - before.ObservedUtc) - (current.DeviceUtc - before.DeviceUtc)).TotalSeconds) > MaximumReadbackAge.TotalSeconds ||
            !double.IsFinite(toleranceArcseconds) || toleranceArcseconds <= 0)
            return Rejected("新帧前后跟踪模式、镜侧、历元或时间顺序改变；不归档旧责任。");
        // Tracking OFF: the mechanical pointing is Earth-fixed, while its RA
        // changes with sidereal time. Do not change tracking to force a match.
        // Tracking ON: retain the original equatorial same-sky test.
        var separation = before.Tracking
            ? G3AcquisitionMotionPlanner.AngularSeparationArcseconds(before.RaDegrees, before.DecDegrees, current.RaDegrees, current.DecDegrees)
            : G3AcquisitionMotionPlanner.AngularSeparationArcseconds(before.AzimuthDegrees, before.AltitudeDegrees, current.AzimuthDegrees, current.AltitudeDegrees);
        var frame = before.Tracking ? "赤道坐标（跟踪开启）" : "地平坐标（跟踪关闭）";
        return separation <= toleranceArcseconds
            ? GateResult.Pass("GUIDE_STARTUP_MOUNT_STATIONARY", $"{frame}位移 {separation:F2}″ ≤ {toleranceArcseconds:F2}″；仅证明停止边界，不证明原图仍可入缝。")
            : Rejected($"{frame}位移 {separation:F2}″ > {toleranceArcseconds:F2}″，未确认停止；旧记录保留。");
    }

    // NINA owns the driver. Await a newly completed owner poll after capture;
    // do not open another ASCOM instance or treat a cached pre-frame poll as fresh.
    internal static async Task<StartupGuideMountReadback> ReadFreshAsync(Func<StartupGuideMountReadback> read,
        string expectedMountId, double toleranceArcseconds, Func<CancellationToken, Task> check,
        CancellationToken token, TimeSpan? waitLimit = null)
    {
        var elapsed = Stopwatch.StartNew();
        var limit = waitLimit ?? MaximumReadbackAge;
        token.ThrowIfCancellationRequested();
        var baseline = read();
        do
        {
            token.ThrowIfCancellationRequested();
            await check(token).ConfigureAwait(false);
            var sample = read();
            var gate = Validate(baseline, sample, expectedMountId, toleranceArcseconds);
            if (gate.Disposition != GateDisposition.Passed) throw new PhysicalActionGateException(gate);
            // Compare the clock's progress, NOT its absolute UTC offset. A
            // stopped mount with a stable clock offset still needs the normal
            // startup clock-repair path after old control records are retired.
            token.ThrowIfCancellationRequested();
            if (sample.DeviceUtc > baseline.DeviceUtc) return sample;
            await Task.Delay(TimeSpan.FromMilliseconds(100), token).ConfigureAwait(false);
        } while (elapsed.Elapsed < limit);
        throw new PhysicalActionGateException(Rejected("未等到曝光完成后的新赤道仪轮询；没有归档旧责任。"));
    }

    private static GateResult Rejected(string detail) => GateResult.Unknown("GUIDE_STARTUP_STOP_UNCONFIRMED", detail);
}
