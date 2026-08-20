using System.ComponentModel.Composition;
using NINA.Core.Model;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using NINA.Sequencer.SequenceItem;
using Newtonsoft.Json;
using UvexAdv.Core;
using UvexAdv.Spectroscopy;

namespace UvexAdv.Nina.Plugin.SequenceItems;

public abstract class UvexSequenceItemBase : SequenceItem
{
    protected readonly IProfileService ProfileService;
    protected readonly ICameraMediator CameraMediator;
    protected readonly IImagingMediator ImagingMediator;

    protected UvexSequenceItemBase(IProfileService profileService, ICameraMediator cameraMediator, IImagingMediator imagingMediator)
    {
        ProfileService = profileService;
        CameraMediator = cameraMediator;
        ImagingMediator = imagingMediator;
    }

    protected UvexSequenceItemBase(UvexSequenceItemBase copy)
        : this(copy.ProfileService, copy.CameraMediator, copy.ImagingMediator)
    {
        CopyMetaData(copy);
    }

    private protected UvexPluginSettings Settings => new(ProfileService);

    private protected UvexServiceClient CreateClient() => new(Settings.ServiceUrl);

    private protected static void RequireStableAtrBinding(UvexPluginSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.BoundCameraId))
        {
            throw new InvalidOperationException(
                "A stable ATR585M DeviceId is required for a spectral closed loop. Bind the current camera from the OpenAstroSpec Spectrum panel first.");
        }
    }

    protected static void Report(IProgress<ApplicationStatus> progress, string message, double value = -1) =>
        progress.Report(new ApplicationStatus { Source = "OpenAstroSpec Auto", Status = message, Progress = value });
}

[ExportMetadata("Name", "等待 UVEX 就绪")]
[ExportMetadata("Description", "等待独立 UVEX4 控制服务连接并确认所有位置可信")]
[ExportMetadata("Category", "OpenAstroSpec Auto")]
[Export(typeof(ISequenceItem))]
[JsonObject(MemberSerialization.OptIn)]
public sealed class WaitUvexReadyItem : UvexSequenceItemBase
{
    [ImportingConstructor]
    public WaitUvexReadyItem(IProfileService profile, ICameraMediator camera, IImagingMediator imaging) : base(profile, camera, imaging) { }
    private WaitUvexReadyItem(WaitUvexReadyItem copy) : base(copy) { TimeoutSeconds = copy.TimeoutSeconds; }

    [JsonProperty]
    public int TimeoutSeconds { get; set; } = 30;

    public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token)
    {
        Report(progress, "等待 UVEX 服务就绪");
        using var client = CreateClient();
        await client.WaitReadyAsync(TimeSpan.FromSeconds(Math.Clamp(TimeoutSeconds, 1, 300)), token).ConfigureAwait(false);
    }

    public override object Clone() => new WaitUvexReadyItem(this);
}

[ExportMetadata("Name", "UVEX 光栅回零")]
[ExportMetadata("Description", "通过独立服务执行光栅零级回零")]
[ExportMetadata("Category", "OpenAstroSpec Auto")]
[Export(typeof(ISequenceItem))]
[JsonObject(MemberSerialization.OptIn)]
public sealed class HomeGratingItem : UvexSequenceItemBase
{
    [ImportingConstructor]
    public HomeGratingItem(IProfileService profile, ICameraMediator camera, IImagingMediator imaging) : base(profile, camera, imaging) { }
    private HomeGratingItem(HomeGratingItem copy) : base(copy) { }

    public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token)
    {
        Report(progress, "光栅回零");
        using var client = CreateClient();
        await using var lease = await client.AcquireLeaseAsync("N.I.N.A. sequence", token).ConfigureAwait(false);
        var operation = await client.HomeGratingAsync(lease.Token, token).ConfigureAwait(false);
        await client.WaitForOperationAsync(operation, token).ConfigureAwait(false);
    }

    public override object Clone() => new HomeGratingItem(this);
}

[ExportMetadata("Name", "UVEX 设置名义波长")]
[ExportMetadata("Description", "使用 UVEX 固件的名义波长定位，不替代后续相机闭环")]
[ExportMetadata("Category", "OpenAstroSpec Auto")]
[Export(typeof(ISequenceItem))]
[JsonObject(MemberSerialization.OptIn)]
public sealed class GotoWavelengthItem : UvexSequenceItemBase
{
    [ImportingConstructor]
    public GotoWavelengthItem(IProfileService profile, ICameraMediator camera, IImagingMediator imaging) : base(profile, camera, imaging) { }
    private GotoWavelengthItem(GotoWavelengthItem copy) : base(copy) { WavelengthNm = copy.WavelengthNm; }

    [JsonProperty]
    public double WavelengthNm { get; set; } = 550;

    public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token)
    {
        Report(progress, $"移动至名义波长 {WavelengthNm:F1} nm");
        using var client = CreateClient();
        await using var lease = await client.AcquireLeaseAsync("N.I.N.A. sequence", token).ConfigureAwait(false);
        var operation = await client.GotoWavelengthAsync(WavelengthNm, lease.Token, token).ConfigureAwait(false);
        await client.WaitForOperationAsync(operation, token).ConfigureAwait(false);
    }

    public override object Clone() => new GotoWavelengthItem(this);
}

[ExportMetadata("Name", "UVEX 选择狭缝")]
[ExportMetadata("Description", "选择 1-4 号 UVEX 狭缝位置")]
[ExportMetadata("Category", "OpenAstroSpec Auto")]
[Export(typeof(ISequenceItem))]
[JsonObject(MemberSerialization.OptIn)]
public sealed class SelectSlitItem : UvexSequenceItemBase
{
    [ImportingConstructor]
    public SelectSlitItem(IProfileService profile, ICameraMediator camera, IImagingMediator imaging) : base(profile, camera, imaging) { }
    private SelectSlitItem(SelectSlitItem copy) : base(copy) { Position = copy.Position; }

    [JsonProperty]
    public int Position { get; set; } = 1;

    public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token)
    {
        Report(progress, $"选择狭缝 {Position}");
        using var client = CreateClient();
        await using var lease = await client.AcquireLeaseAsync("N.I.N.A. sequence", token).ConfigureAwait(false);
        var operation = await client.SelectSlitAsync(Position, lease.Token, token).ConfigureAwait(false);
        await client.WaitForOperationAsync(operation, token).ConfigureAwait(false);
    }

    public override object Clone() => new SelectSlitItem(this);
}

[ExportMetadata("Name", "UVEX 移动 M2")]
[ExportMetadata("Description", "按相对步数移动光谱仪 M2 对焦机构")]
[ExportMetadata("Category", "OpenAstroSpec Auto")]
[Export(typeof(ISequenceItem))]
[JsonObject(MemberSerialization.OptIn)]
public sealed class MoveFocusItem : UvexSequenceItemBase
{
    [ImportingConstructor]
    public MoveFocusItem(IProfileService profile, ICameraMediator camera, IImagingMediator imaging) : base(profile, camera, imaging) { }
    private MoveFocusItem(MoveFocusItem copy) : base(copy) { DeltaSteps = copy.DeltaSteps; }

    [JsonProperty]
    public int DeltaSteps { get; set; } = 50;

    public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token)
    {
        Report(progress, $"移动 M2 {DeltaSteps:+#;-#;0} steps");
        using var client = CreateClient();
        await using var lease = await client.AcquireLeaseAsync("N.I.N.A. sequence", token).ConfigureAwait(false);
        var operation = await client.MoveFocusAsync(DeltaSteps, lease.Token, token).ConfigureAwait(false);
        await client.WaitForOperationAsync(operation, token).ConfigureAwait(false);
    }

    public override object Clone() => new MoveFocusItem(this);
}

[ExportMetadata("Name", "UVEX 光谱自动对焦")]
[ExportMetadata("Description", "使用多条谱线 FWHM 和 7 点曲线闭环调整 UVEX M2")]
[ExportMetadata("Category", "OpenAstroSpec Auto")]
[Export(typeof(ISequenceItem))]
[JsonObject(MemberSerialization.OptIn)]
public sealed class SpectralAutofocusItem : UvexSequenceItemBase
{
    [ImportingConstructor]
    public SpectralAutofocusItem(IProfileService profile, ICameraMediator camera, IImagingMediator imaging) : base(profile, camera, imaging) { }
    private SpectralAutofocusItem(SpectralAutofocusItem copy) : base(copy) { }

    public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token)
    {
        var settings = Settings;
        if (!settings.SpectralAutofocusCommissioned)
        {
            throw new InvalidOperationException(
                "UVEX M2 spectral autofocus is not independently commissioned; automatic M2 movement remains disabled.");
        }

        RequireStableAtrBinding(settings);

        var lines = settings.ParseFocusLines();
        if (lines.Count < 3)
        {
            throw new InvalidOperationException("Configure at least three focus line pixel positions in UVEX plugin options.");
        }
        if (settings.FocusStepSize <= 0 || settings.FocusMinimum >= settings.FocusMaximum || settings.FocusBacklash < 0)
        {
            throw new InvalidOperationException(
                "UVEX M2 autofocus requires a positive scan step, ordered absolute travel limits and a non-negative preload distance.");
        }

        using var client = CreateClient();
        await client.WaitReadyAsync(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
        await using var lease = await client.AcquireLeaseAsync("N.I.N.A. spectral autofocus", token).ConfigureAwait(false);
        var status = await client.GetStatusAsync(token).ConfigureAwait(false) ?? throw new InvalidOperationException("UVEX status is unavailable.");
        var capture = new NinaSpectrumCapture(CameraMediator, ImagingMediator, settings, progress);
        var context = new NinaClosedLoopContext(client, lease, capture, status);
        Report(progress, "开始 7 点谱线自动对焦", 0);
        var result = await SpectralAutofocusEngine.RunAsync(context, new AutofocusOptions(
            settings.FocusStepSize,
            7,
            settings.FocusMinimum,
            settings.FocusMaximum,
            lines,
            settings.FocusBacklash), token).ConfigureAwait(false);
        await LoopRunLogger.WriteAsync("spectral-autofocus", result, CancellationToken.None).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(result.FailureReason);
        }

        Report(progress, $"光谱对焦完成：{result.FinalPositionSteps} steps，FWHM {result.VerificationMetric?.FwhmPixels:F2} px", 1);
    }

    public override object Clone() => new SpectralAutofocusItem(this);
}

[ExportMetadata("Name", "UVEX 波长闭环")]
[ExportMetadata("Description", "使用指定参考谱线质心将波长锁定到目标像素")]
[ExportMetadata("Category", "OpenAstroSpec Auto")]
[Export(typeof(ISequenceItem))]
[JsonObject(MemberSerialization.OptIn)]
public sealed class WavelengthLockItem : UvexSequenceItemBase
{
    [ImportingConstructor]
    public WavelengthLockItem(IProfileService profile, ICameraMediator camera, IImagingMediator imaging) : base(profile, camera, imaging) { }
    private WavelengthLockItem(WavelengthLockItem copy) : base(copy) { }

    public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token)
    {
        var settings = Settings;
        if (!settings.WavelengthLockCommissioned)
        {
            throw new InvalidOperationException(
                "UVEX grating wavelength lock is not independently commissioned; automatic grating movement remains disabled.");
        }

        RequireStableAtrBinding(settings);
        if (!double.IsFinite(settings.WavelengthReferencePixel) ||
            !double.IsFinite(settings.WavelengthTargetPixel) ||
            !double.IsFinite(settings.GratingStepsPerPixel) ||
            settings.GratingStepsPerPixel == 0)
        {
            throw new InvalidOperationException("Wavelength lock is not commissioned. Configure the reference pixel, target pixel and grating steps/pixel.");
        }

        using var client = CreateClient();
        await client.WaitReadyAsync(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
        await using var lease = await client.AcquireLeaseAsync("N.I.N.A. wavelength lock", token).ConfigureAwait(false);
        var status = await client.GetStatusAsync(token).ConfigureAwait(false) ?? throw new InvalidOperationException("UVEX status is unavailable.");
        var capture = new NinaSpectrumCapture(CameraMediator, ImagingMediator, settings, progress);
        var context = new NinaClosedLoopContext(client, lease, capture, status);
        Report(progress, "开始波长闭环", 0);
        var result = await WavelengthLockEngine.RunAsync(context, new WavelengthLockOptions(
            new SpectralLineWindow(settings.WavelengthReferencePixel),
            settings.WavelengthTargetPixel,
            settings.GratingStepsPerPixel), token).ConfigureAwait(false);
        await LoopRunLogger.WriteAsync("wavelength-lock", result, CancellationToken.None).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(result.FailureReason);
        }

        Report(progress, $"波长闭环完成：{result.FinalPositionSteps} steps", 1);
    }

    public override object Clone() => new WavelengthLockItem(this);
}

[ExportMetadata("Name", "UVEX 维护模式")]
[ExportMetadata("Description", "释放或重新接管 COM5；用于与旧 DRIVER.UVEX4 互斥切换")]
[ExportMetadata("Category", "OpenAstroSpec Auto")]
[Export(typeof(ISequenceItem))]
[JsonObject(MemberSerialization.OptIn)]
public sealed class MaintenanceModeItem : UvexSequenceItemBase
{
    [ImportingConstructor]
    public MaintenanceModeItem(IProfileService profile, ICameraMediator camera, IImagingMediator imaging) : base(profile, camera, imaging) { }
    private MaintenanceModeItem(MaintenanceModeItem copy) : base(copy) { EnterMaintenance = copy.EnterMaintenance; }

    [JsonProperty]
    public bool EnterMaintenance { get; set; } = true;

    public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token)
    {
        using var client = CreateClient();
        await using var lease = await client.AcquireLeaseAsync("N.I.N.A. maintenance", token).ConfigureAwait(false);
        var operation = EnterMaintenance
            ? await client.EnterMaintenanceAsync(lease.Token, token).ConfigureAwait(false)
            : await client.ExitMaintenanceAsync(lease.Token, token).ConfigureAwait(false);
        await client.WaitForOperationAsync(operation, token).ConfigureAwait(false);
    }

    public override object Clone() => new MaintenanceModeItem(this);
}
