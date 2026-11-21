using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Model;
using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

internal sealed partial class RealObservationStageRunner : IWindowsRestartRun
{
    WindowsRestartRunPort IWindowsRestartRun.RestartPort => new(WindowsRestartProbe.System, CloseForWindowsRestartAsync);

    internal async Task<IReadOnlyList<string>> CloseForWindowsRestartAsync(bool verifyLocalOwners, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(configuration.Environment.RoofCloseoutTimeoutSeconds +
            configuration.Environment.OpticalCoverTransitionTimeoutSeconds + 180 +
            (configuration.SequencePlan?.HomeBeforeParkOnFinalize == true ? configuration.SequencePlan.MountHomeTimeoutSeconds : 0)));
        BoundRoofPosition Read() => CurrentRoofBoundaryGate() is not null
            ? BoundRoofPosition.Unconfirmed : NinaRoofReadback.Position(domeMediator.GetInfo(), configuration.Environment.Devices.DomeOrRoofId);
        var errors = await WindowsRestartCloseout.RunAsync(Read, configuration.Environment.WeakSupervisionEnabled,
            ct => CheckedRestartCloseoutAsync(verifyLocalOwners, ct), timeout.Token,
            TimeSpan.FromSeconds(configuration.Environment.DomeOrRoofTransitionTimeoutSeconds)).ConfigureAwait(false);
        await WriteAuditBestEffortAsync("windows-update-closeout", new { reason = WindowsRestartProbe.Code,
            errors, confirmedWithoutErrors = errors.Count == 0, automaticallyRestarted = false }).ConfigureAwait(false);
        return errors;
    }

    internal async Task<IReadOnlyList<string>> CheckedRestartCloseoutAsync(bool verifyLocalOwners, CancellationToken token)
    {
        var auth = ObservationAutomationPolicy.AuthorizeExecutionMode(true, settings.ObservationUseRealMode, settings.RealModeCommissioned);
        if (auth.Disposition != GateDisposition.Passed) return new[] { auth.Message };
        string? MasterIssue() => RestartMasterOwnerIssue(cameraMediator.GetInfo(), configuration.NightSetup.AtrStableId,
            telescopeMediator.GetInfo(), configuration.ExpectedTelescopeId);
        // A standby closer has no local jobs of its own. Do not treat an empty
        // dictionary or a different idle camera as proof of the original owners.
        var boundCamera = cameraMediator.GetInfo();
        var boundMount = telescopeMediator.GetInfo();
        if (!boundCamera.Connected || boundCamera.DeviceId != configuration.NightSetup.AtrStableId ||
            !boundMount.Connected || boundMount.DeviceId != configuration.ExpectedTelescopeId)
            return ["原绑定 ATR 或赤道仪身份未确认，未接管其他设备。"];
        if (verifyLocalOwners)
        {
            var issues = await ConfirmSequenceIdleAsync(token, requireSettledMotion: false).ConfigureAwait(false);
            if (issues.Count != 0) return issues;
        }
        if (configuration.Qhy.SynchronizedPhotometryEnabled || !activeQhyJobs.IsEmpty || !pendingQhyRequests.IsEmpty)
        {
            var idle = await qhy.ReadGlobalIdleAsync(token).ConfigureAwait(false);
            if (idle.Disposition != GateDisposition.Passed) return [$"测光所有者未确认停止：{idle.Code} · {idle.Message}"];
        }
        if (MasterIssue() is { } changed) return [changed];
        return await CloseSequenceNightAsync(token).ConfigureAwait(false);
    }

    internal static string? RestartMasterOwnerIssue(CameraInfo camera, string cameraId, TelescopeInfo mount, string mountId)
    {
        if (string.IsNullOrWhiteSpace(cameraId) || !camera.Connected || camera.DeviceId != cameraId || camera.IsExposing || camera.LiveViewEnabled)
            return "未确认原绑定 ATR 已停止曝光及预览；未授权机械收口。";
        if (string.IsNullOrWhiteSpace(mountId) || !mount.Connected || mount.DeviceId != mountId || mount.Slewing || mount.IsPulseGuiding)
            return "未确认原绑定赤道仪已停止转向和脉冲；未授权机械收口。";
        return null;
    }
}
