using System.IO;
using System.Text.Json;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Image.Interfaces;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Utility.AutoFocus;
using OxyPlot.Series;
using UvexAdv.Observatory;
using UvexAdv.Phd2;

namespace UvexAdv.Nina.Plugin;

internal sealed class NinaSepMainFocusHardware(
    IProfileService profiles, IFocuserMediator focuser, ITelescopeMediator mount, ICameraMediator camera,
    IDomeMediator roof, IFlatDeviceMediator cover, ISafetyMonitorMediator safety,
    IImageDataFactory images, UvexPluginSettings settings, SepMainFocusOptions options) : ISepMainFocusHardware, IAsyncDisposable
{
    private readonly Phd2Client phd = new(new Phd2ClientOptions { Host = settings.Phd2Host, Port = settings.Phd2Port });
    private readonly SepStarDetectionClient sep = new();
    private bool connected;
    private string? binding;
    private double initialRa, initialDec;
    private (int Width, int Height)? dimensions;
    private readonly SepDetectionRuntime runtime = SepStarDetectionClient.ReadRuntime(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UVEX-ADV", "star-detection", "runtime.json"));

    public async Task<SepFocusState> ReadReadyAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        NinaInstancePolicy.RequireMaster(settings);
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
        deadline.CancelAfter(TimeSpan.FromSeconds(40));
        // This is NINA's native backlash-compensated owner route, not ASCOM direct access.
        var actual = await focuser.MoveFocuser(position, deadline.Token);
        if (actual != position) throw new InvalidOperationException("N.I.N.A. 主镜移动未到达请求位置。");
        await Task.Delay(2000, deadline.Token);
    }

    public async Task<SepFocusFrame> CaptureAsync(string newPath, SepFocusReference[]? reference, CancellationToken token)
    {
        if (File.Exists(newPath)) throw new IOException("禁止覆盖原始对焦帧。");
        var captured = await phd.CaptureSingleFrameWithParametersAsync(new Phd2SingleFrameRequest(
            options.ExposureMilliseconds, 1, options.GainPercent, newPath), token);
        var image = await images.CreateFromFile(captured.Path, 16, false, NINA.Core.Enum.RawConverterEnum.FREEIMAGE, token);
        if (image.Properties.IsBayered) throw new InvalidDataException("主镜 SEP 对焦需要单色原始帧。");
        var currentDimensions = (image.Properties.Width, image.Properties.Height);
        if (dimensions is not null && dimensions.Value != currentDimensions)
            throw new InvalidDataException("对焦期间导星相机图像尺寸发生变化；不混用不同像素尺度的曲线。");
        dimensions = currentDimensions;
        return await sep.MeasureFocusAsync(runtime, image.Properties.Width, image.Properties.Height,
            image.Data.FlatArray, options.Saturation, reference, token);
    }

    public Task<SepFocusFit> FitAsync(SepFocusPoint[] points, CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        var fit = new HyperbolicFitting().Calculate(points.Select(p => new ScatterErrorPoint(p.Position, p.R50, 0, p.Error)).ToList());
        if (fit.Fitting is null) throw new InvalidOperationException("N.I.N.A. 未得到可用的主镜对焦曲线。");
        token.ThrowIfCancellationRequested();
        return new SepFocusFit((int)fit.Minimum.X, fit.RSquared);
    }, token);

    public ValueTask DisposeAsync() => phd.DisposeAsync();
}
