using System.IO;
using System.Text.Json;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Interfaces;
using NINA.Image.Interfaces;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Utility.AutoFocus;
using OxyPlot.Series;
using UvexAdv.Observatory;
using UvexAdv.Phd2;

namespace UvexAdv.Nina.Plugin;

internal sealed class SepFocusMeasurementException(string message, Exception inner) : Exception(message, inner);

internal sealed class NinaSepMainFocusHardware(
    IProfileService profiles, IFocuserMediator focuser, ITelescopeMediator mount, ICameraMediator camera,
    IDomeMediator roof, IFlatDeviceMediator cover, ISafetyMonitorMediator safety,
    IImageDataFactory images, UvexPluginSettings settings, SepMainFocusOptions options,
    SepDetectionRuntime? lockedRuntime = null) : ISepMainFocusHardware, IAsyncDisposable
{
    private readonly Phd2Client phd = new(new Phd2ClientOptions { Host = settings.Phd2Host, Port = settings.Phd2Port });
    private readonly SepStarDetectionClient sep = new();
    private bool connected;
    private readonly Guid profileId = profiles.ActiveProfile.Id;
    private readonly string selectedFocuser = profiles.ActiveProfile.FocuserSettings.Id;
    private readonly string phdHost = settings.Phd2Host;
    private readonly int phdPort = settings.Phd2Port, phdProfile = settings.Phd2ProfileId;
    private readonly string phdCamera = settings.Phd2RuntimeCameraName;
    private IFocuser? focusOwner;
    private bool focusCommandIssued, captureCommandIssued;
    private long? connectionEpoch;
    private DateTimeOffset lastCompleted;
    private Task<int>? nativeMove;
    private Task<Phd2SingleFrameResult>? nativeCapture;
    internal Task? PendingBoundaryOperation { get; set; }
    private CancellationTokenSource? nativeMoveCancellation, nativeCaptureCancellation;
    private string? binding;
    private double initialRa, initialDec;
    private (int Width, int Height)? dimensions;
    internal static string RuntimePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UVEX-ADV", "star-detection", "runtime.json");
    private readonly SepDetectionRuntime runtime = lockedRuntime ?? SepStarDetectionClient.ReadRuntime(RuntimePath);

    internal static SepDetectionRuntime? ReadOptionalRuntime(string path, out string? issue)
    {
        try
        {
            var candidate = SepStarDetectionClient.ReadRuntime(path);
            if (!File.Exists(Path.Combine(Path.GetDirectoryName(candidate.WorkerPath)!, "focus_metrics.py")))
                throw new InvalidDataException("缺少 focus_metrics.py。");
            issue = null;
            return candidate;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            issue = ex.Message;
            return null;
        }
    }

    public async Task<SepFocusState> ReadReadyAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (nativeMove is { IsCompleted: false } || nativeCapture is { IsCompleted: false })
            throw new InvalidOperationException("原生对焦动作仍未返回；禁止并发采集或回位。");
        NinaInstancePolicy.RequireMaster(settings);
        if (profiles.ActiveProfile.Id != profileId || profiles.ActiveProfile.FocuserSettings.Id != selectedFocuser
            || settings.Phd2Host != phdHost || settings.Phd2Port != phdPort
            || settings.Phd2ProfileId != phdProfile || settings.Phd2RuntimeCameraName != phdCamera)
            throw new InvalidOperationException("对焦 Profile、主镜或 PHD2 所有者设置已改变；不切换设备。");
        var f = focuser.GetInfo(); var m = mount.GetInfo(); var c = cover.GetInfo(); var d = roof.GetInfo(); var s = safety.GetInfo();
        var selected = profiles.ActiveProfile.FocuserSettings.Id;
        if (!f.Connected || f.DeviceId != selected || string.IsNullOrWhiteSpace(selected) || f.Position < 0 || f.IsMoving || f.IsSettling || f.TempComp)
            throw new InvalidOperationException("主镜电调焦必须为当前 N.I.N.A. 选定设备，已连接、停止且关闭自动温补。");
        if (!m.Connected || m.Slewing || m.IsPulseGuiding || m.AtPark || !m.TrackingEnabled
            || !double.IsFinite(m.Altitude) || m.Altitude < options.MinimumAltitude || !double.IsFinite(m.RightAscension) || !double.IsFinite(m.Declination))
            throw new InvalidOperationException("请先将目标置于安全高度并保持恒星跟踪；对焦不会开顶或转向。");
        if (!d.Connected || d.ShutterStatus.ToString() != "ShutterOpen" || !c.Connected || c.CoverState.ToString() != "Open" || c.LightOn
            || (s.Connected && !s.IsSafe) || camera.GetInfo().IsExposing)
            throw new InvalidOperationException("对焦要求已开顶、镜盖打开、平场灯关闭、无科学曝光且没有不安全信号。");
        if (!File.Exists(Path.Combine(Path.GetDirectoryName(runtime.WorkerPath)!, "focus_metrics.py")))
            throw new InvalidOperationException("请先安装更新后的 SEP 图像运行环境（缺少 focus_metrics.py）。");
        var focus = profiles.ActiveProfile.FocuserSettings;
        if (focus.BacklashCompensationModel == NINA.Core.Enum.BacklashCompensationModel.ABSOLUTE)
            throw new InvalidOperationException("绝对回差模型隐藏物理位置偏移；主镜扫焦请使用已标定的 None 或 Overshoot。");
        var now = JsonSerializer.Serialize(new { profile = profiles.ActiveProfile.Id, f.DeviceId,
            mount = m.DeviceId, pier = m.SideOfPier, camera = settings.Phd2RuntimeCameraName, phdProfile = settings.Phd2ProfileId,
            settings.Phd2Host, settings.Phd2Port,
            focus.BacklashCompensationModel, focus.BacklashIn, focus.BacklashOut });
        if (m.SideOfPier.ToString() is not ("pierEast" or "pierWest")) throw new InvalidOperationException("赤道仪侧位未知。");
        if (binding is not null && (binding != now || Math.Abs(m.RightAscension - initialRa) > .001 || Math.Abs(m.Declination - initialDec) > .015))
            throw new InvalidOperationException("对焦期间设备配置、侧位或赤道仪坐标发生改变；停止新动作。");
        if (!connected) { await phd.ConnectAsync(token); connected = true; }
        if (connectionEpoch is { } epoch && phd.Snapshot.ConnectionEpoch != epoch)
            throw new InvalidOperationException("PHD2 对焦连接纪元改变；旧图像与停止证明不再复用。");
        connectionEpoch ??= phd.Snapshot.ConnectionEpoch;
        var owner = focuser.GetDevice() as IFocuser;
        if (owner is null || focusOwner is not null && !ReferenceEquals(focusOwner, owner))
            throw new InvalidOperationException("主镜调焦原生所有者发生改变。");
        focusOwner ??= owner;
        var profile = await phd.GetProfileAsync(token);
        var equipment = await phd.GetCurrentEquipmentAsync(token);
        if (profile.Id != settings.Phd2ProfileId || equipment.Camera?.Name != settings.Phd2RuntimeCameraName || !equipment.Camera.Connected
            || await phd.GetAppStateAsync(token) != Phd2AppState.Stopped)
            throw new InvalidOperationException("PHD2 必须停止且仍持有绑定的光谱仪导星相机；不会停止别人的导星或切换相机。");
        if (binding is null) { binding = now; initialRa = m.RightAscension; initialDec = m.Declination; }
        var overshoot = focus.BacklashCompensationModel == NINA.Core.Enum.BacklashCompensationModel.OVERSHOOT;
        return new(f.Position, now, overshoot ? Math.Max(0, focus.BacklashIn) : 0, overshoot ? Math.Max(0, focus.BacklashOut) : 0);
    }

    public async Task MoveAsync(int position, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        nativeMoveCancellation = deadline;
        deadline.CancelAfter(TimeSpan.FromSeconds(40));
        // This is NINA's native backlash-compensated owner route, not ASCOM direct access.
        focusCommandIssued = true;
        nativeMove = focuser.MoveFocuser(position, deadline.Token);
        var actual = await nativeMove.WaitAsync(deadline.Token);
        if (actual != position) throw new InvalidOperationException("N.I.N.A. 主镜移动未到达请求位置。");
        await Task.Delay(2000, deadline.Token);
    }

    public async Task<SepFocusFrame> CaptureAsync(string newPath, SepFocusReference[]? reference, CancellationToken token)
    {
        if (File.Exists(newPath)) throw new IOException("禁止覆盖原始对焦帧。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        nativeCaptureCancellation = deadline;
        captureCommandIssued = true;
        nativeCapture = phd.CaptureSingleFrameWithParametersAsync(new Phd2SingleFrameRequest(
            options.ExposureMilliseconds, 1, options.GainPercent, newPath), deadline.Token);
        var captured = await nativeCapture.ConfigureAwait(false);
        if (!captured.RequestedParametersApplied || captured.UsedLoopSaveFallback
            || captured.VerifiedExposureMilliseconds != options.ExposureMilliseconds || captured.CompletedUtc <= lastCompleted)
            throw new InvalidDataException("原生对焦帧未确认曝光/增益/合并设置或时间新鲜性。");
        lastCompleted = captured.CompletedUtc;
        var image = await images.CreateFromFile(captured.Path, 16, false, NINA.Core.Enum.RawConverterEnum.FREEIMAGE, token);
        if (image.Properties.IsBayered) throw new InvalidDataException("主镜 SEP 对焦需要单色原始帧。");
        var currentDimensions = (image.Properties.Width, image.Properties.Height);
        if (dimensions is not null && dimensions.Value != currentDimensions)
            throw new InvalidDataException("对焦期间导星相机图像尺寸发生变化；不混用不同像素尺度的曲线。");
        dimensions = currentDimensions;
        try
        {
            return await sep.MeasureFocusAsync(runtime, image.Properties.Width, image.Properties.Height,
                image.Data.FlatArray, options.Saturation, reference, token);
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
        {
            // This wrapper is deliberately limited to optional image analysis.
            // Capture identity, freshness, parameters and raw-file checks above
            // remain hardware/evidence failures, never this quality fallback.
            throw new SepFocusMeasurementException("SEP 焦质测量不可用：" + ex.Message, ex);
        }
    }

    public Task<SepFocusFit> FitAsync(SepFocusPoint[] points, CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        var fit = new HyperbolicFitting().Calculate(points.Select(p => new ScatterErrorPoint(p.Position, p.R50, 0, p.Error)).ToList());
        if (fit.Fitting is null) throw new InvalidOperationException("N.I.N.A. 未得到可用的主镜对焦曲线。");
        token.ThrowIfCancellationRequested();
        return new SepFocusFit((int)fit.Minimum.X, fit.RSquared);
    }, token);

    // Stop-only owner route for automatic recovery. A cancelled Move task is
    // not evidence of a stopped motor. Never connect, move or change owners here.
    internal static async Task<bool> TryJoinOwnerOperationAsync(Task operation, TimeSpan wait, CancellationToken token)
    {
        try { await operation.WaitAsync(wait, token).ConfigureAwait(false); return true; }
        catch when (operation.IsCompleted) { return true; }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { return false; }
    }

    internal async Task<IReadOnlyList<string>> StopAndConfirmAsync(CancellationToken token)
    {
        var errors = new List<string>();
        var f = focuser.GetInfo();
        var owner = focuser.GetDevice() as IFocuser;
        if (!f.Connected || f.DeviceId != selectedFocuser || owner is null
            || focusOwner is null || !ReferenceEquals(owner, focusOwner))
            errors.Add("主镜所有者未确认，未对其他设备发停止命令。");
        else
        {
            if (nativeMove is { IsCompleted: false })
                try { nativeMoveCancellation?.Cancel(); } catch (ObjectDisposedException) { /* Already cancelled by Move's deadline. */ }
            if (focusCommandIssued && (owner.IsMoving || f.IsMoving || f.IsSettling)) owner.Halt();
            if (nativeMove is { } pendingMove)
            {
                if (!await TryJoinOwnerOperationAsync(pendingMove, TimeSpan.FromSeconds(10), token).ConfigureAwait(false))
                    errors.Add("原生回差动作任务未结清；瞬时电机停止不能替代原生异步任务结清。");
            }
            var end = DateTimeOffset.UtcNow.AddSeconds(10);
            var stationary = 0;
            var position = owner.Position;
            while (DateTimeOffset.UtcNow < end && stationary < 3)
            {
                token.ThrowIfCancellationRequested();
                f = focuser.GetInfo();
                var actual = owner.Position;
                stationary = !owner.IsMoving && !f.IsMoving && !f.IsSettling && actual == position ? stationary + 1 : 0;
                position = actual;
                await Task.Delay(250, token).ConfigureAwait(false);
            }
            if (stationary < 3 || !f.Connected || f.DeviceId != selectedFocuser || f.Position != position)
                errors.Add("主镜电机停止与稳定位置回读未确认。");
        }
        if (connected)
        {
            var profile = await phd.GetProfileAsync(token).ConfigureAwait(false);
            var equipment = await phd.GetCurrentEquipmentAsync(token).ConfigureAwait(false);
            if (profile.Id != phdProfile || equipment.Camera?.Name != phdCamera || equipment.Camera.Connected != true
                || phd.Snapshot.ConnectionEpoch != connectionEpoch)
                errors.Add("PHD2 原相机/连接纪元未确认，未接管其他会话。");
            else if (captureCommandIssued)
            {
                if (nativeCapture is { IsCompleted: false })
                    try { nativeCaptureCancellation?.Cancel(); } catch (ObjectDisposedException) { /* Already cancelled. */ }
                var stop = await phd.StopCaptureAndConfirmAsync(token).ConfigureAwait(false);
                if (!stop.ConfirmedIdle || stop.FinalState != Phd2AppState.Stopped) errors.Add("PHD2 对焦曝光停止未确认。");
                if (nativeCapture is { } pendingCapture)
                {
                    if (!await TryJoinOwnerOperationAsync(pendingCapture, TimeSpan.FromSeconds(3), token).ConfigureAwait(false))
                        errors.Add("PHD2 原生对焦帧任务未结清。");
                }
            }
            else if (await phd.GetAppStateAsync(token).ConfigureAwait(false) != Phd2AppState.Stopped)
                errors.Add("PHD2 已非停止状态；未停止别人的采集。");
        }
        return errors;
    }

    public ValueTask DisposeAsync() => nativeMove is { IsCompleted: false } || nativeCapture is { IsCompleted: false }
        || PendingBoundaryOperation is { IsCompleted: false }
        ? ValueTask.CompletedTask : phd.DisposeAsync(); // Never tear down an unjoined owner as if cleanup succeeded.
}
