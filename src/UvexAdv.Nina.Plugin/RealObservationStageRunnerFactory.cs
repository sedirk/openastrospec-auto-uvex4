using System.ComponentModel.Composition;
using System.Runtime.Versioning;
using NINA.Core.Model;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Model;
using NINA.Equipment.Equipment.MyDome;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Image.Interfaces;
using NINA.PlateSolving.Interfaces;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.Mediator;
using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

/// <summary>
/// MEF composition boundary for the real runner. Constructing the factory does
/// not connect equipment; physical owners are contacted only by stage methods
/// after commissioning and checkpoint gates pass.
/// </summary>
[Export(typeof(RealObservationStageRunnerFactory))]
[PartCreationPolicy(CreationPolicy.Shared)]
[SupportedOSPlatform("windows")]
public sealed class RealObservationStageRunnerFactory : IDisposable
{
    private readonly ITelescopeMediator telescopeMediator;
    private readonly IGuiderMediator guiderMediator;
    private readonly IFocuserMediator focuserMediator;
    private readonly ICameraMediator cameraMediator;
    private readonly IImagingMediator imagingMediator;
    private readonly IImageSaveMediator imageSaveMediator;
    private readonly IPlateSolverFactory plateSolverFactory;
    private readonly IImageDataFactory imageDataFactory;
    private readonly IProfileService profileService;
    private readonly ISafetyMonitorMediator safetyMonitorMediator;
    private readonly IDomeMediator domeMediator;
    private readonly IWeatherDataMediator weatherDataMediator;
    private readonly IFlatDeviceMediator flatDeviceMediator;
    private readonly IFilterWheelMediator filterWheelMediator;
    private readonly IDomeFollower domeFollower;
    private readonly ISequenceMediator? sequences;
    private object? nativeSequenceCaptureOwner;
    private bool sequenceEventsAttached;
    private readonly object sequenceEventsSync = new();

    [ImportingConstructor]
    public RealObservationStageRunnerFactory(
        ITelescopeMediator telescopeMediator,
        IGuiderMediator guiderMediator,
        IFocuserMediator focuserMediator,
        ICameraMediator cameraMediator,
        IImagingMediator imagingMediator,
        IImageSaveMediator imageSaveMediator,
        IPlateSolverFactory plateSolverFactory,
        IImageDataFactory imageDataFactory,
        IProfileService profileService,
        ISafetyMonitorMediator safetyMonitorMediator,
        IDomeMediator domeMediator,
        IWeatherDataMediator weatherDataMediator,
        IFlatDeviceMediator flatDeviceMediator,
        IFilterWheelMediator filterWheelMediator,
        IDomeFollower domeFollower,
        ISequenceMediator? sequences = null)
    {
        this.telescopeMediator = telescopeMediator;
        this.guiderMediator = guiderMediator;
        this.focuserMediator = focuserMediator;
        this.cameraMediator = cameraMediator;
        this.imagingMediator = imagingMediator;
        this.imageSaveMediator = imageSaveMediator;
        this.plateSolverFactory = plateSolverFactory;
        this.imageDataFactory = imageDataFactory;
        this.profileService = profileService;
        this.safetyMonitorMediator = safetyMonitorMediator;
        this.domeMediator = domeMediator;
        this.weatherDataMediator = weatherDataMediator;
        this.flatDeviceMediator = flatDeviceMediator;
        this.filterWheelMediator = filterWheelMediator;
        this.domeFollower = domeFollower;
        this.sequences = sequences;
    }

    // MEF constructs sequence items while NINA is still building its
    // navigation. Subscribe during native validation, before SequenceStarting,
    // not from the importing constructor or a device-action method.
    internal void ObserveNativeSequenceLifetime()
    {
        lock (sequenceEventsSync)
        {
            if (sequences is null || sequenceEventsAttached) return;
            try { if (!sequences.Initialized) return; }
            catch (NullReferenceException) { return; } // NINA 3.2: navigation not registered yet.
            sequences.SequenceStarting += OnSequenceStarting;
            sequences.SequenceFinished += OnSequenceFinished;
            sequenceEventsAttached = true;
        }
    }

    private Task OnSequenceStarting(object sender, EventArgs args)
    {
        Volatile.Write(ref nativeSequenceCaptureOwner, sender);
        return Task.CompletedTask;
    }

    private Task OnSequenceFinished(object sender, EventArgs args)
    {
        Interlocked.CompareExchange(ref nativeSequenceCaptureOwner, null, sender);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        lock (sequenceEventsSync)
        {
            if (sequences is null || !sequenceEventsAttached) return;
            sequences.SequenceStarting -= OnSequenceStarting;
            sequences.SequenceFinished -= OnSequenceFinished;
            sequenceEventsAttached = false;
            Volatile.Write(ref nativeSequenceCaptureOwner, null);
        }
    }

    internal RealObservationStageRunner Create(
        ObservationCoordinatorHost host,
        UvexPluginSettings settings,
        IProgress<ApplicationStatus> progress,
        RealRunConfiguration? lockedConfiguration = null) => new(
            host,
            settings,
            lockedConfiguration ?? CaptureConfiguration(settings),
            telescopeMediator,
            guiderMediator,
            focuserMediator,
            cameraMediator,
            imagingMediator,
            imageSaveMediator,
            plateSolverFactory,
            imageDataFactory,
            profileService,
            safetyMonitorMediator,
            domeMediator,
            weatherDataMediator,
            flatDeviceMediator,
            filterWheelMediator,
            domeFollower,
            progress,
            () => sequences?.IsAdvancedSequenceRunning() == true ? Volatile.Read(ref nativeSequenceCaptureOwner) : null);

    internal RealRunConfiguration CaptureConfiguration(UvexPluginSettings settings)
    {
        NinaInstancePolicy.RequireMaster(settings);
        var plateSettings = profileService.ActiveProfile.PlateSolveSettings;
        var primarySolver = plateSolverFactory.GetPlateSolver(plateSettings);
        var blindSolver = plateSolverFactory.GetBlindSolver(plateSettings);
        return RealRunConfiguration.Capture(
            settings,
            PlateSolverRunConfiguration.Capture(plateSettings, primarySolver, blindSolver),
            profileService.ActiveProfile.ImageFileSettings.FilePattern,
            CaptureEnvironmentDeviceSelection());
    }

    internal NinaSepMainFocusHardware CreateMainFocusHardware(UvexPluginSettings settings, UvexAdv.Observatory.SepMainFocusOptions options)
        => new(profileService, focuserMediator, telescopeMediator, cameraMediator, domeMediator,
            flatDeviceMediator, safetyMonitorMediator, imageDataFactory, settings, options);

    internal IDisposable WatchNightSafety(Action unsafeCallback, bool abortExposureOnUnsafe = true)
    {
        EventHandler<IsSafeEventArgs> handler = (_, e) =>
        {
            if (e.IsSafe) return;
            // An active observing owner aborts its exposure. A post-run guardian
            // only reports/latches safety; unknown stop is not camera authority.
            try { if (abortExposureOnUnsafe) cameraMediator.AbortExposure(); }
            catch { /* The owner records stop/closeout readback failures. */ }
            try { unsafeCallback(); }
            catch (Exception ex)
            {
                // Event publishers must remain able to notify every owner.
                // This is diagnostic containment, not stopped/closed proof.
                NINA.Core.Utility.Logger.Error(new InvalidOperationException(
                    "NIGHT_UNSAFE_CALLBACK_FAILED: safety notification failed; physical closeout remains unconfirmed.", ex));
            }
        };
        safetyMonitorMediator.IsSafeChanged += handler;
        return new SafetySubscription(() => safetyMonitorMediator.IsSafeChanged -= handler);
    }

    private sealed class SafetySubscription(Action unsubscribe) : IDisposable
    {
        public void Dispose() => unsubscribe();
    }

    internal bool IsNightSafetyCurrent(RealRunConfiguration configuration)
    {
        var info = safetyMonitorMediator.GetInfo();
        return info.Connected && info.IsSafe &&
            string.Equals(info.DeviceId, configuration.Environment.Devices.SafetyMonitorId, StringComparison.Ordinal) &&
            CaptureEnvironmentDeviceSelection() == configuration.Environment.Devices;
    }

    // Pre-start Closed/Unknown must not even construct a hardware runner: its
    // normal disposal includes owner reconciliation. Only the live night lease
    // and a positively Open, unchanged binding may enter the existing closer.
    internal Task<IReadOnlyList<string>> CloseNightForWindowsRestartAsync(ObservationCoordinatorHost host,
        UvexPluginSettings settings, RealRunConfiguration configuration, IProgress<ApplicationStatus> progress,
        CancellationToken token) => WindowsRestartCloseout.RunAsync(
            () => NinaRoofConnectionBoundary.For(domeMediator).PendingGate is not null ||
                  CaptureEnvironmentDeviceSelection() != configuration.Environment.Devices
                ? BoundRoofPosition.Unconfirmed : NinaRoofReadback.Position(domeMediator.GetInfo(), configuration.Environment.Devices.DomeOrRoofId),
            configuration.Environment.WeakSupervisionEnabled, async ct =>
            {
                await using var closer = Create(host, settings, progress, configuration);
                return await closer.CheckedRestartCloseoutAsync(false, ct).ConfigureAwait(false);
            }, token, TimeSpan.FromSeconds(configuration.Environment.DomeOrRoofTransitionTimeoutSeconds));

    // Called only for an explicitly requested new night after the old runner
    // attested settled acquisition/motion responsibilities. No runner creation
    // (its disposal performs cleanup), adapter Connect, stop or motion commands.
    internal async Task<GateResult> RecheckStandbyIdleAsync(RealRunConfiguration configuration, CancellationToken token)
    {
        GateResult ReadIdle()
        {
            if (NinaRoofConnectionBoundary.For(domeMediator).PendingGate is { } pendingRoof) return pendingRoof;
            if (CaptureEnvironmentDeviceSelection() != configuration.Environment.Devices)
                return GateResult.Unknown("NIGHT_STANDBY_DEVICE_SELECTION_CHANGED", "当前 Profile 的安全/屋顶/镜盖绑定已改变，请核对原台站配置；不会接管其他设备。");
            if (!IsNightSafetyCurrent(configuration))
                return GateResult.Unknown("NIGHT_SAFETY_UNAVAILABLE", "原绑定安全监视器未连接、不安全或身份不符；不能重新开始观测。");
            var camera = cameraMediator.GetInfo();
            var mount = telescopeMediator.GetInfo();
            var focus = focuserMediator.GetInfo();
            var roof = domeMediator.GetInfo();
            if (!camera.Connected)
                return GateResult.Unknown("NIGHT_STANDBY_ATR_DISCONNECTED", "ATR 未连接，不能证明曝光已停止；请在 N.I.N.A. 连接原光谱相机后重新开始序列，不需要重启程序。");
            if (camera.DeviceId != configuration.NightSetup.AtrStableId)
                return GateResult.Unknown("NIGHT_STANDBY_ATR_IDENTITY_CHANGED", "连接的 ATR 身份与上一轮不同，不能接管原设备责任。");
            if (camera.IsExposing || camera.LiveViewEnabled)
                return GateResult.Unknown("NIGHT_STANDBY_ATR_BUSY", "ATR 仍在曝光或实时预览；请先停止，再重新开始序列。");
            if (!mount.Connected)
                return GateResult.Unknown("NIGHT_STANDBY_MOUNT_DISCONNECTED", "赤道仪未连接，不能证明运动已停止；请在 N.I.N.A. 连接原赤道仪后重新开始序列，不需要重启程序。");
            if (mount.DeviceId != configuration.ExpectedTelescopeId)
                return GateResult.Unknown("NIGHT_STANDBY_MOUNT_IDENTITY_CHANGED", "连接的赤道仪身份与上一轮不同，不能接管原运动责任。");
            if (mount.Slewing || mount.IsPulseGuiding || focus.IsMoving || focus.IsSettling)
                return GateResult.Unknown("NIGHT_STANDBY_MOTION_UNCONFIRMED", "赤道仪转向/脉冲或主镜对焦尚未停止；请先核对停止状态，再重新开始序列。");
            return CheckStandbyRoofReadback(roof, configuration.Environment.Devices.DomeOrRoofId);
        }
        var initial = await WaitForStandbyReadbackAsync(ReadIdle, token).ConfigureAwait(false);
        if (initial.Disposition != GateDisposition.Passed) return initial;
        await using var phd = new UvexAdv.Phd2.Phd2Client(new()
        { Host = configuration.Phd2Host, Port = configuration.Phd2Port });
        await phd.ConnectAsync(token).ConfigureAwait(false); // Event-server socket, not a camera connection.
        var phdState = await phd.GetAppStateAsync(token).ConfigureAwait(false);
        if (phdState != UvexAdv.Phd2.Phd2AppState.Stopped)
            return GateResult.Unknown("NIGHT_STANDBY_PHD2_NOT_STOPPED", $"PHD2 当前为 {phdState}，未确认停止；请先停止导星/循环预览后重新开始序列。");
        var quiet = await Phd2MountIdleWait.WaitAfterTargetStopAsync(phd, telescopeMediator,
            configuration.ExpectedTelescopeId, token).ConfigureAwait(false);
        if (quiet.Disposition != GateDisposition.Passed) return quiet;
        var final = ReadIdle();
        return final.Disposition == GateDisposition.Passed
            ? GateResult.Pass("NIGHT_STANDBY_IDLE_RECHECKED", "原绑定设备、PHD2 及赤道仪连续停止已重新确认；没有连接相机或发出运动命令。") : final;
    }

    internal static GateResult CheckStandbyRoofReadback(DomeInfo roof, string expectedRoofId)
    {
        if (!roof.Connected)
            return GateResult.Unknown("NIGHT_STANDBY_ROOF_DISCONNECTED", "原绑定屋顶接口未连接；请恢复该接口并核对实际屋顶状态后重试，不会自动开关顶。");
        if (roof.DeviceId != expectedRoofId)
            return GateResult.Unknown("NIGHT_STANDBY_ROOF_IDENTITY_CHANGED", "连接的屋顶身份与上一轮不同，不能沿用原防碰撞绑定。");
        if (NinaRoofReadback.Position(roof, expectedRoofId) is not (BoundRoofPosition.Open or BoundRoofPosition.Closed))
            return GateResult.Unknown("NIGHT_STANDBY_ROOF_STATE_UNCONFIRMED",
                $"屋顶读回为 {roof.ShutterStatus}，不是已开/已关的停止终态；请检查屋顶驱动和现场状态。未知/错误状态不能当作已开顶。");
        return GateResult.Pass("NIGHT_STANDBY_IDLE_READBACK", "原绑定设备停止读回已核对；尚须独立确认 PHD2 停止。");
    }

    // N.I.N.A. publishes the first connected device snapshot asynchronously.
    // Spend at most three seconds of the existing 20 s handover deadline waiting
    // for that readback; do not connect devices or interpret Unknown as Open.
    internal static async Task<GateResult> WaitForStandbyReadbackAsync(Func<GateResult> read,
        CancellationToken token, TimeSpan? maximumWait = null, TimeSpan? pollInterval = null)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        var limit = maximumWait ?? TimeSpan.FromSeconds(3);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var gate = read();
            if (gate.Disposition == GateDisposition.Passed || started.Elapsed >= limit || gate.Code is not
                ("NIGHT_STANDBY_ATR_DISCONNECTED" or "NIGHT_STANDBY_MOUNT_DISCONNECTED" or
                 "NIGHT_STANDBY_ROOF_DISCONNECTED" or "NIGHT_STANDBY_ROOF_STATE_UNCONFIRMED")) return gate;
            await Task.Delay(pollInterval ?? TimeSpan.FromMilliseconds(100), token).ConfigureAwait(false);
        }
    }

    private NinaEnvironmentDeviceSelection CaptureEnvironmentDeviceSelection()
    {
        var profile = profileService.ActiveProfile;
        return new NinaEnvironmentDeviceSelection(
            profile.SafetyMonitorSettings.Id ?? NinaEnvironmentDeviceSelection.NoDeviceId,
            profile.DomeSettings.Id ?? NinaEnvironmentDeviceSelection.NoDeviceId,
            profile.WeatherDataSettings.Id ?? NinaEnvironmentDeviceSelection.NoDeviceId,
            profile.FlatDeviceSettings.Id ?? NinaEnvironmentDeviceSelection.NoDeviceId);
    }
}
