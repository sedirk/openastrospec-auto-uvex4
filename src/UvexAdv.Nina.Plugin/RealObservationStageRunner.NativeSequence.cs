using NINA.Astrometry;
using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

internal sealed partial class RealObservationStageRunner
{
    private GateResult? SequenceStopGate() => configuration.SequencePlan?.StopGate(DateTimeOffset.UtcNow,
        time => AstroUtil.GetSunAltitude(time.UtcDateTime, new ObserverInfo
        {
            Latitude = configuration.SequencePlan.LatitudeDegrees,
            Longitude = configuration.SequencePlan.LongitudeDegrees,
        }));

    internal async Task<IReadOnlyList<string>> ConfirmSequenceIdleAsync(CancellationToken token, bool requireSettledMotion = true)
    {
        var errors = (await CleanupAfterFailureAsync("Native sequence target boundary", token,
            allowMechanicalActions: false).ConfigureAwait(false)).ToList();
        // Unconfirmed flip intent prohibits another target, not emergency
        // closeout after all owners and physical motion are confirmed idle.
        if (requireSettledMotion && NativeMeridianJournalStore.PendingGate(NativeMeridianJournalPath) is { } flipGate)
            errors.Add(flipGate.Message);
        var camera = cameraMediator.GetInfo();
        if (!camera.Connected || camera.IsExposing) errors.Add("ATR 未确认连接且曝光空闲。");
        var telescope = telescopeMediator.GetInfo();
        if (!telescope.Connected || telescope.Slewing) errors.Add("赤道仪未确认连接且停止运动。");
        if (activeQhyJobs.Count != 0 || pendingQhyRequests.Count != 0) errors.Add("测光作业尚未确认结束。");
        if (requireSettledMotion && (pendingSlitPlacement is { Phase: not SlitPlacementPendingPhase.SettledBudgetLedger } ||
            pendingPhd2LockShift is { Phase: not Phd2LockShiftPendingPhase.SettledBudgetLedger } ||
            durableG3AcquisitionMotion is { Phase: not G3AcquisitionMotionPhase.SettledBudgetLedger }))
            errors.Add("运动回程责任未结清，禁止跳目标或声称收口成功。");
        return errors;
    }

    // The night owner calls this only after all target tasks/owners have joined.
    // Reuse the exact selected-adapter, parked-before-roof and checked-terminal
    // implementation used by the single-target production route.
    internal async Task<IReadOnlyList<string>> CloseSequenceNightAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var errors = new List<string>();
        if (!cameraMediator.GetInfo().Connected || cameraMediator.GetInfo().IsExposing ||
            !telescopeMediator.GetInfo().Connected || telescopeMediator.GetInfo().Slewing)
            return new[] { "设备仍忙；本次未发起整夜机械收口。" };
        if (configuration.Environment.CloseOpticalCoverOnFinalize)
        {
            var issue = await CloseOpticalCoverAsync("Native sequence night closeout", token).ConfigureAwait(false);
            if (issue is not null) errors.Add(issue);
        }
        if (configuration.Environment.CloseDomeOrRoofOnFinalize)
        {
            token.ThrowIfCancellationRequested();
            var issue = await ParkMountAndCloseDomeOrRoofAsync("Native sequence night closeout", token).ConfigureAwait(false);
            if (issue is not null) errors.Add(issue);
        }
        await WriteAuditBestEffortAsync("native-sequence-night-closeout", new
        {
            errors, cover = flatDeviceMediator.GetInfo().CoverState.ToString(),
            parked = telescopeMediator.GetInfo().AtPark, roof = domeMediator.GetInfo().ShutterStatus.ToString(),
            weakSupervision = configuration.Environment.WeakSupervisionEnabled,
        }).ConfigureAwait(false);
        return errors;
    }
}
