using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class SepMainFocusViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly UvexPluginSettings settings;
    private readonly ObservationCoordinatorHost host;
    private readonly RealObservationStageRunnerFactory factory;
    private readonly Func<bool> canUse;
    private readonly Func<string> profileId;
    private readonly Action changed;
    private readonly SimpleAsyncCommand start;
    private readonly SimpleCommand cancel, save, open;
    private CancellationTokenSource? cancellation;
    private SepMainFocusOptions options;
    private SepMainFocusResult? result;
    private string status = "尚未对焦。请先配置主镜软限位；启动只使用已连接设备，不开顶、不转向。";
    public event PropertyChangedEventHandler? PropertyChanged;

    internal SepMainFocusViewModel(UvexPluginSettings settings, ObservationCoordinatorHost host,
        RealObservationStageRunnerFactory factory, Func<bool> canUse, Func<string> profileId, Action changed)
    {
        this.settings = settings; this.host = host; this.factory = factory; this.canUse = canUse;
        this.profileId = profileId; this.changed = changed;
        try { options = JsonSerializer.Deserialize<SepMainFocusOptions>(settings.SepMainFocusOptionsJson) ?? new(); }
        catch (JsonException) { options = new(); }
        start = new(StartAsync, () => !IsBusy && canUse());
        cancel = new(() => { cancellation?.Cancel(); SetStatus("已请求取消，等待当前操作停止并在安全条件允许时回到原位…"); }, () => IsBusy);
        save = new(() => { settings.SepMainFocusOptionsJson = JsonSerializer.Serialize(options); SetStatus("主镜对焦参数已保存到当前 N.I.N.A. Profile；未操作设备。"); }, () => !IsBusy && canUse());
        open = new(() => Process.Start(new ProcessStartInfo("explorer.exe", EvidenceDirectory) { UseShellExecute = true }), () => Directory.Exists(EvidenceDirectory));
        try
        {
            var path = settings.SepMainFocusLastResultPath;
            if (File.Exists(path) && new FileInfo(path).Length < 8_000_000)
            {
                result = JsonSerializer.Deserialize<SepMainFocusResult>(File.ReadAllText(path));
                if (result is not null) status = "上次记录（不是当前实报）：" + result.Message;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { status = "上次结果无法读取：" + ex.Message; }
    }

    public bool IsBusy => cancellation is not null;
    public bool IsExpanded { get; set; }
    public bool CanEdit => !IsBusy;
    public string Status => status;
    public string EvidenceDirectory => result?.Directory ?? "";
    public SepMainFocusResult? Result => result;
    public IReadOnlyList<SepFocusPoint> Curve => result?.Curve ?? [];
    public PointCollection CurvePoints
    {
        get
        {
            var a = Curve.OrderBy(x => x.Position).ToArray();
            if (a.Length < 2) return [];
            double xmin = a.Min(x => x.Position), xmax = a.Max(x => x.Position), ymax = a.Max(x => x.R50), ymin = a.Min(x => x.R50);
            return new(a.Select(p => new Point(10 + (p.Position - xmin) / Math.Max(1, xmax - xmin) * 330,
                95 - (p.R50 - ymin) / Math.Max(.1, ymax - ymin) * 85)));
        }
    }
    public int MinimumPosition { get => options.MinimumPosition; set { options = options with { MinimumPosition = value }; Notify(); } }
    public int MaximumPosition { get => options.MaximumPosition; set { options = options with { MaximumPosition = value }; Notify(); } }
    public int Step { get => options.Step; set { options = options with { Step = value }; Notify(); } }
    public int HalfPoints { get => options.HalfPoints; set { options = options with { HalfPoints = value }; Notify(); } }
    public int Frames { get => options.Frames; set { options = options with { Frames = value }; Notify(); } }
    public int ExposureMilliseconds { get => options.ExposureMilliseconds; set { options = options with { ExposureMilliseconds = value }; Notify(); } }
    public int GainPercent { get => options.GainPercent; set { options = options with { GainPercent = value }; Notify(); } }
    public double Saturation { get => options.Saturation; set { options = options with { Saturation = value }; Notify(); } }
    public double MinimumAltitude { get => options.MinimumAltitude; set { options = options with { MinimumAltitude = value }; Notify(); } }
    public ICommand StartCommand => start;
    public ICommand CancelCommand => cancel;
    public ICommand SaveCommand => save;
    public ICommand OpenEvidenceCommand => open;

    internal void Refresh() { start.RaiseCanExecuteChanged(); save.RaiseCanExecuteChanged(); cancel.RaiseCanExecuteChanged(); }
    private void Notify([System.Runtime.CompilerServices.CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
    private void SetStatus(string text) { status = text; Notify(nameof(Status)); changed(); }
    private async Task StartAsync()
    {
        if (IsBusy || !canUse()) return;
        var capturedProfile = profileId();
        var locked = options with { };
        settings.SepMainFocusOptionsJson = JsonSerializer.Serialize(locked);
        cancellation = new CancellationTokenSource();
        Notify(nameof(IsBusy)); Notify(nameof(CanEdit)); Refresh(); changed();
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UVEX-ADV", "main-focus",
                $"{DateTime.UtcNow:yyyyMMddTHHmmss}-{Guid.NewGuid():N}");
            SetStatus("核对观测占用、软限位、设备身份和开顶状态；不会连接未连接的物理设备。");
            result = await host.RunFocusPreparationAsync(async () =>
            {
                await using var hardware = factory.CreateMainFocusHardware(settings, locked);
                return await new SepMainFocusRunner(hardware).RunAsync(locked, directory, new Progress<string>(SetStatus), cancellation.Token);
            }, cancellation.Token);
            if (capturedProfile == profileId()) settings.SepMainFocusLastResultPath = Path.Combine(directory, "result.json");
            SetStatus(result.Message);
            Notify(nameof(Result)); Notify(nameof(Curve)); Notify(nameof(CurvePoints)); Notify(nameof(EvidenceDirectory));
            open.RaiseCanExecuteChanged();
        }
        catch (Exception ex) { SetStatus("主镜对焦未完成：" + ex.Message); }
        finally
        {
            cancellation.Dispose(); cancellation = null;
            Notify(nameof(IsBusy)); Notify(nameof(CanEdit)); Refresh(); changed();
        }
    }
    public void Dispose() => cancellation?.Cancel();
}
