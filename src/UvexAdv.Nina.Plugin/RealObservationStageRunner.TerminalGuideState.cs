using System.IO;
using UvexAdv.Observatory;
using UvexAdv.Phd2;

namespace UvexAdv.Nina.Plugin;

internal sealed partial class RealObservationStageRunner : IObservationTerminalGuideCleanup
{
    public async Task CleanupTerminalGuideStateAsync(string runId, CancellationToken token)
    {
        // A meridian segment is NOT the end of its logical target. It must keep
        // the original motion budget and fresh re-entry obligations.
        if (NativeMeridian?.Requested == true) return;
        retiredClockBudget ??= CaptureClockContinuationBudget();
        // A preflight failure may never have connected a camera or produced any
        // guide authority. Do not turn that into a spurious cleanup failure.
        if (!File.Exists(Phd2LockShiftPendingPath(runId)) && !File.Exists(G3AcquisitionMotionPath(runId)) &&
            pendingPhd2LockShift is null && durableG3AcquisitionMotion is null) return;
        var idleErrors = await ConfirmSequenceIdleAsync(token, requireSettledMotion: false).ConfigureAwait(false);
        if (idleErrors.Count != 0) throw new InvalidOperationException(string.Join("; ", idleErrors));
        if (pendingSlitPlacement is { Phase: not SlitPlacementPendingPhase.SettledBudgetLedger } ||
            NativeMeridianJournalStore.PendingGate(NativeMeridianJournalPath) is not null)
            throw new InvalidOperationException("仍有非导星状态的机构/翻转责任，不能自动清除。");

        var archives = await GuideStateBoundaryRetirement.RetireAsync(SlitPlacementObservationsRoot(), runId,
            ReadTerminalGuideWitnessAsync, token).ConfigureAwait(false);
        pendingPhd2LockShift = null;
        durableG3AcquisitionMotion = null;
        pendingG3SearchReturn = null;
        phd2SlitPlacementSession = null;
        phd2CompletedInitialGuideStopReceipt = null;
        lastG3Field = null;
        validatedG3GuideConnectionEpoch = null;
        validatedG3GuideEpoch = null;
        Report($"目标已结束：已归档 {archives.Count} 份导星旧状态；未声称回位成功，下一目标重新解算，PHD2 校准不变。");
    }

    private async Task<GuideBoundaryWitness> ReadTerminalGuideWitnessAsync(CancellationToken token)
    {
        // Only an already-owned RPC connection is read. No reconnect, guide,
        // lock shift, recalibration, home, park or roof command in this path.
        var matches = false;
        var state = Phd2AppState.Unknown;
        if (phd2.IsConnected)
        {
            matches = (await phd2.ValidateIdentityAsync(PhdIdentityRequirement(), token).ConfigureAwait(false)).IsValid;
            state = await phd2.GetAppStateAsync(token).ConfigureAwait(false);
        }
        var camera = cameraMediator.GetInfo();
        var mount = telescopeMediator.GetInfo();
        var focus = focuserMediator.GetInfo();
        return new(activeQhyJobs.IsEmpty && pendingQhyRequests.IsEmpty && !focus.IsMoving,
            host.RealRunOwnershipGate().Disposition == GateDisposition.Passed,
            camera.Connected && !camera.IsExposing && camera.DeviceId == configuration.NightSetup.AtrStableId,
            mount.Connected && !mount.Slewing && !mount.IsPulseGuiding,
            mount.DeviceId, configuration.Phd2.CameraStableId, state, matches, DateTimeOffset.UtcNow);
    }
}
