using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Equipment.Interfaces;
using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

internal sealed partial class RealObservationStageRunner
{
    private readonly AtrCoolingRecoveryPolicy atrCoolingRecovery = new();
    private CancellationTokenSource? atrPreCoolingCancellation;

    private async Task<GateResult> ReadFreshAtrCoolingReadinessAsync(string expectedId, CancellationToken token)
    {
        if (cameraMediator.GetDevice() is not ICamera owner)
            return GateResult.Unknown("ATR_COOLING_DISCONNECTED", "N.I.N.A. does not have an ATR owner.");
        try
        {
            var sample = await ReadFreshAtrOwnerAsync(owner, expectedId, expectedId, token).ConfigureAwait(false);
            return AtrCoolingReadinessPolicy.Evaluate(sample, expectedId, configuration.Atr.TargetTemperatureC);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return GateResult.Unknown("ATR_COOLING_FRESH_READ_FAILED", $"同一 ATR 拥有者实时温度读取失败；未开始曝光：{ex.Message}");
        }
    }

    private async Task<GateResult> RecoverAtrCoolingAsync(ObservationContext context,
        AtrCoolingRecoveryAction action, CancellationToken token)
    {
        if (action == AtrCoolingRecoveryAction.Exhausted)
            return GateResult.Unknown("ATR_COOLING_RECOVERY_EXHAUSTED",
                "ATR 已重新设温并重连同一相机各一次，温度读回仍无改善；未开始曝光。请检查供电、制冷能力和驱动，未放宽温度门。");
        await atrCoolingCommandGate.WaitAsync(token).ConfigureAwait(false);
        var captureBlock = new object();
        var blockHeld = false;
        try
        {
            var expectedId = context.Plan.ExpectedAtrCameraId;
            var expectedProfile = profileService.ActiveProfile;
            var owner = cameraMediator.GetDevice();
            GateResult IdleIdentityGate()
            {
                var info = cameraMediator.GetInfo();
                if (!info.Connected || info.DeviceId != expectedId ||
                    !ReferenceEquals(expectedProfile, profileService.ActiveProfile) ||
                    profileService.ActiveProfile.CameraSettings.Id != expectedId ||
                    !ReferenceEquals(owner, cameraMediator.GetDevice()))
                    return GateResult.Unknown("ATR_COOLING_RECOVERY_OWNER_CHANGED", "ATR 连接或 Profile 身份已改变；未自动恢复或选择另一设备。");
                if (info.IsExposing || info.CameraState is not (CameraStates.Idle or CameraStates.NoState) ||
                    !cameraMediator.IsFreeToCapture(captureBlock))
                    return GateResult.Unknown("ATR_COOLING_RECOVERY_CAMERA_BUSY", "ATR 正在曝光、读出或被另一任务占用；未重连、未中断采集。");
                return GateResult.Pass("ATR_COOLING_RECOVERY_IDLE", "同一 ATR 拥有者空闲。");
            }
            var idle = IdleIdentityGate();
            if (idle.Disposition != GateDisposition.Passed) return idle;
            cameraMediator.RegisterCaptureBlock(captureBlock);
            blockHeld = true;
            // Cancel ONLY our cooling task and await its finally/cancel handler:
            // NINA's handler writes a set-point on cancellation. Never race that
            // old write against a reconnected owner or the new target command.
            if (atrPreCoolingTask is { } oldTask)
            {
                atrPreCoolingCancellation?.Cancel();
                try { await oldTask.WaitAsync(TimeSpan.FromSeconds(20), token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
                catch (Exception) when (oldTask.IsCompleted && !token.IsCancellationRequested) { }
                if (!oldTask.IsCompleted) return GateResult.Unknown("ATR_COOLING_RECOVERY_STOP_UNCONFIRMED", "旧制冷任务尚未停止；未重连相机。");
                atrPreCoolingTask = null;
            }
            token.ThrowIfCancellationRequested();
            idle = IdleIdentityGate();
            if (idle.Disposition != GateDisposition.Passed) return idle;
            Volatile.Write(ref atrStableTemperatureEstablished, 0);
            await WriteAuditBestEffortAsync("atr-cooling-recovery-intent", new
            {
                context.Plan.ObservationRunId, action = action.ToString(), expectedId,
                targetTemperatureC = configuration.Atr.TargetTemperatureC,
                actionsUsed = atrCoolingRecovery.ActionsUsed, exposureStarted = false,
            }).ConfigureAwait(false);
            if (action == AtrCoolingRecoveryAction.ReconnectOwner)
            {
                Report("ATR 温度读回持续不变：相机已确认空闲，由主 N.I.N.A. 重连同一设备一次；不影响导星、测光和机构。");
                // Native mediator only. No SDK handle, rescan, profile switch or
                // retry loop. Await native completion even on user cancellation;
                // then honor cancellation before any subsequent command.
                await cameraMediator.Disconnect().ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (cameraMediator.GetInfo().Connected || !ReferenceEquals(expectedProfile, profileService.ActiveProfile) ||
                    profileService.ActiveProfile.CameraSettings.Id != expectedId)
                    return GateResult.Unknown("ATR_COOLING_RECOVERY_DISCONNECT_UNCONFIRMED", "ATR 断开或同一 Profile 身份未确认；停止恢复。");
                if (!await cameraMediator.Connect().ConfigureAwait(false))
                    return GateResult.Unknown("ATR_COOLING_RECOVERY_CONNECT_FAILED", "N.I.N.A. 未能重连原 ATR；未开始曝光。");
                token.ThrowIfCancellationRequested();
                owner = cameraMediator.GetDevice();
                idle = IdleIdentityGate();
                if (idle.Disposition != GateDisposition.Passed) return idle;
                cameraMediator.SetReadoutModeForNormalImages((short)configuration.Atr.ReadoutModeIndex);
                cameraMediator.SetBinning(configuration.Atr.Binning, configuration.Atr.Binning);
            }
            Report($"ATR 制冷恢复：重新要求 {configuration.Atr.TargetTemperatureC:F1}°C；等待真实温度稳定，不立即开始曝光。");
            atrPreCoolingCancellation?.Dispose();
            atrPreCoolingCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            atrPreCoolingTask = cameraMediator.CoolCamera(configuration.Atr.TargetTemperatureC, TimeSpan.Zero,
                new Progress<ApplicationStatus>(_ => { }), atrPreCoolingCancellation.Token);
            await WriteAuditBestEffortAsync("atr-cooling-recovery-commanded", new
            {
                context.Plan.ObservationRunId, action = action.ToString(), expectedId,
                targetTemperatureC = configuration.Atr.TargetTemperatureC,
                stableTemperatureClaimed = false, exposureStarted = false,
            }).ConfigureAwait(false);
            return GateResult.Pass("ATR_COOLING_RECOVERY_COMMAND_SENT", "ATR 恢复制冷命令已发出，仍须重新测量并通过稳定温度门。");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return GateResult.Unknown("ATR_COOLING_RECOVERY_FAILED", $"ATR 制冷恢复未完成；未开始曝光：{ex.Message}");
        }
        finally
        {
            if (blockHeld) cameraMediator.ReleaseCaptureBlock(captureBlock);
            atrCoolingCommandGate.Release();
        }
    }
}
