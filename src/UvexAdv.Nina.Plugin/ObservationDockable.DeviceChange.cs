using System.IO;
using System.Windows;
using System.Windows.Input;

namespace UvexAdv.Nina.Plugin;

public sealed partial class ObservationDockable
{
    private string DeviceChangeText(string chinese, string english) => ObservationUiPresentation.Text(chinese, english, UiCulture);
    private bool deviceChangeBusy;
    private SimpleCommand? deviceChangeCommand;
    public ICommand ResolveDeviceChangeCommand => deviceChangeCommand ??= new SimpleCommand(
        ResolveDeviceChange, () => CanEditTargetPlan() && !HasSafetyStandby);

    private RoofDriverChange? ReadRoofDriverChange()
    {
        if (string.IsNullOrWhiteSpace(settings.SelectedCommissioningProfilePath))
            throw new InvalidOperationException(DeviceChangeText("尚未选择设备标定。请在自动准备中选择已有方案。", "Select an existing commissioning setup in Preparation first."));
        var profile = activeProfileService.ActiveProfile;
        return CommissioningRoofDriverChange.Inspect(settings.SelectedCommissioningProfilePath,
            profile.TelescopeSettings.Id, profile.DomeSettings.Id, settings.CommissioningPresetSha256);
    }

    private async void ResolveDeviceChange()
    {
        if (!CanEditTargetPlan() || HasSafetyStandby) return;
        try
        {
            var change = ReadRoofDriverChange();
            if (change is null)
            {
                RecheckPreparation();
                ShowStartupRequirementsCommand.Execute(null);
                OperatorNotice = DeviceChangeText("已就地重新核对配置。屋顶驱动无待确认变化；其他问题及处理入口见下方。若更换了相机、赤道仪或光学安装，请点击“更换安装标定”。未连接或移动设备。",
                    "Setup rechecked. No roof-driver change needs confirmation; remaining issues and actions are below. Use Change commissioning setup for a different camera, mount or optical installation. No devices were connected or moved.");
                return;
            }
            var confirmation = MessageBox.Show(
                DeviceChangeText($"屋顶驱动已更换\n\n原驱动：{change.OldRoof}\n当前驱动：{change.NewRoof}\n\n是否仍为同一座屋顶、同一赤道仪，而且原先确认的所有姿态防碰撞净空没有改变？\n\n选择“是”将保存新绑定并重新校验，保留原标定。不会连接、开关屋顶或启动观测。若物理设备或安装改变，请选“否”后重新标定。",
                    $"Roof driver changed\n\nPrevious: {change.OldRoof}\nCurrent: {change.NewRoof}\n\nIs this the same physical roof and mount, with the original all-position clearance unchanged?\n\nYes saves a new binding and validates it, keeping the original. No device connection, roof motion or observation is started. Select No if physical equipment or installation changed."),
                DeviceChangeText("确认设备变更", "Confirm device change"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (confirmation != MessageBoxResult.Yes) return;
            if (!CanEditTargetPlan() || HasSafetyStandby) return;
            var profileId = activeProfileService.ActiveProfile.Id;
            deviceChangeBusy = true;
            RaiseCommandStates();
            await host.RunIdleConfigurationAsync(async () =>
            {
                var destination = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UVEX-ADV", "commissioning");
                var path = await CommissioningRoofDriverChange.CreateAsync(change, true, destination,
                    token: lifetime.Token).ConfigureAwait(true);
                if (activeProfileService.ActiveProfile.Id != profileId || ReadRoofDriverChange() != change)
                    throw new InvalidOperationException("处理期间 Profile 或设备选择发生变化；新文件已保留，但没有应用，请重新检查。");
                // A driver-only change must not reload old exposure, target or
                // movement settings from the parent commissioning package.
                var reference = CommissioningRoofDriverChange.ReadReference(path);
                var candidate = realRunnerFactory.CaptureConfiguration(settings);
                candidate = candidate with { Commissioning = candidate.Commissioning with
                {
                    RealModeCommissioned = true, PresetPath = reference.PresetPath,
                    PresetId = reference.PresetId, PresetSha256 = reference.PresetSha256,
                    HardwareFingerprintSha256 = reference.HardwareFingerprintSha256,
                } };
                var checkedPreset = await RealCommissioningPresetLoader.LoadAsync(candidate, lifetime.Token);
                if (checkedPreset.Preset is null || checkedPreset.Issues.Count != 0)
                    throw new InvalidDataException(string.Join("; ", checkedPreset.Issues));
                var checkedNight = await LockedNightSetupSnapshotLoader.LoadAsync(candidate,
                    ObservationPlanFactory.FromSettings(settings, candidate), checkedPreset.Preset, lifetime.Token);
                if (checkedNight.Issues.Count != 0)
                    throw new InvalidDataException(string.Join("; ", checkedNight.Issues));
                if (activeProfileService.ActiveProfile.Id != profileId || ReadRoofDriverChange() != change)
                    throw new InvalidOperationException("校验期间设备选择变化；未应用新绑定，请重新检查。");
                settings.RealModeCommissioned = false;
                settings.CommissioningPresetPath = reference.PresetPath;
                settings.CommissioningPresetId = reference.PresetId;
                settings.CommissioningPresetSha256 = reference.PresetSha256;
                settings.CommissioningHardwareFingerprintSha256 = reference.HardwareFingerprintSha256;
                settings.SelectedCommissioningProfileId = settings.SelectedCommissioningProfilePath = Path.GetFullPath(path);
                RefreshCommissioningProfileCatalog(applySelected: false);
                var applied = commissioningProfiles.Single(p => string.Equals(p.BindingsPath, path, StringComparison.OrdinalIgnoreCase));
                CommissioningProfileLoadStatus = TryAutoApproveSelectedCommissioningPackage(applied, explicitRecheck: true);
                Error = settings.RealModeCommissioned ? string.Empty : CommissioningProfileLoadStatus;
                OperatorNotice = DeviceChangeText("设备变更新绑定已保存，原标定保留。", "New device binding saved; original commissioning retained. ")+CommissioningProfileLoadStatus+
                    DeviceChangeText(" 未连接设备、未开关屋顶、未启动观测。", " No device connection, roof motion or observation was started.");
                ShowStartupRequirementsCommand.Execute(null);
                return true;
            }, lifetime.Token).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Error = DeviceChangeText("设备变更处理未完成：", "Device change not completed: ")+ex.Message;
            ShowStartupRequirementsCommand.Execute(null);
        }
        finally
        {
            deviceChangeBusy = false;
            RaisePropertyChanged(string.Empty);
            RaiseCommandStates();
        }
    }
}
