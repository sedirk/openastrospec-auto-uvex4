using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed partial class SepMainFocusViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly UvexPluginSettings settings;
    private readonly ObservationCoordinatorHost host;
    private readonly RealObservationStageRunnerFactory factory;
    private readonly Func<string?> unavailableReason;
    private readonly Func<string> profileId;
    private readonly Action changed;
    private readonly SimpleAsyncCommand start;
    private readonly SimpleCommand cancel, open;
    private readonly SepFocusSaveCommand save;
    private CancellationTokenSource? cancellation;
    private SepMainFocusOptions options = new();
    private SepMainFocusResult? result;
    private string status = "尚未开始对焦。";
    private readonly HashSet<object> invalidEditors = [];
    private bool reloadPending;
    private int selectedTabIndex;
    public event PropertyChangedEventHandler? PropertyChanged;

    internal SepMainFocusViewModel(UvexPluginSettings settings, ObservationCoordinatorHost host,
        RealObservationStageRunnerFactory factory, Func<string?> unavailableReason, Func<string> profileId, Action changed)
    {
        this.settings = settings; this.host = host; this.factory = factory; this.unavailableReason = unavailableReason;
        this.profileId = profileId; this.changed = changed;
        start = new(StartAsync, () => StartBlockReason is null);
        cancel = new(() => { cancellation?.Cancel(); SetStatus("正在取消：停止新采样，等待当前操作结束；仅在安全且位置可信时返回原焦位。"); }, () => IsBusy);
        // Saving a draft is not hardware control and need not wait for an unrelated observation.
        save = new(() => options, value =>
        {
            options = value;
            settings.SepMainFocusOptionsJson = JsonSerializer.Serialize(options);
            Notify(string.Empty); Refresh();
            SetStatus("已保存到当前 N.I.N.A. 配置；未启动对焦。" + SamplingSummary);
        }, () => !IsBusy && !HasInputErrors);
        open = new(() => Process.Start(new ProcessStartInfo("explorer.exe", EvidenceDirectory) { UseShellExecute = true }), () => Directory.Exists(EvidenceDirectory));
        ReloadProfile();
    }

    internal void ReloadProfile()
    {
        if (IsBusy) { reloadPending = true; cancellation?.Cancel(); return; }
        try { options = JsonSerializer.Deserialize<SepMainFocusOptions>(settings.SepMainFocusOptionsJson) ?? new(); }
        catch (JsonException) { options = new(); }
        result = null; invalidEditors.Clear(); status = "尚未开始对焦。";
        ResetChart();
        try
        {
            var path = settings.SepMainFocusLastResultPath;
            if (File.Exists(path) && new FileInfo(path).Length < 8_000_000)
            {
                result = JsonSerializer.Deserialize<SepMainFocusResult>(File.ReadAllText(path));
                if (result is not null) { status = "上次记录（不是当前设备状态）：" + result.Message; UpdateCurve(result.Curve); }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { status = "上次记录无法读取：" + ex.Message; }
        Notify(string.Empty); Refresh();
    }

    public bool IsBusy => cancellation is not null;
    internal static string? ObservationBlockReason(ObservationRunState state) =>
        state == ObservationRunState.Cancelling ? "观测正在取消并收尾；请等顶部显示“已取消”后再对焦。" :
        state is ObservationRunState.Idle or ObservationRunState.Completed or ObservationRunState.Cancelled or ObservationRunState.Faulted ? null :
        "观测仍占用设备。请先取消观测并等待收尾完成；暂停不会释放设备。";
    public bool CanEdit => !IsBusy;
    public bool HasInputErrors => invalidEditors.Count > 0;
    internal void SetEditorErrors(object editor, bool invalid)
    {
        if (invalid) invalidEditors.Add(editor); else invalidEditors.Remove(editor);
        Notify(nameof(HasInputErrors)); Refresh();
    }
    public string? StartBlockReason => IsBusy ? "对焦正在运行；请等待完成，或点击取消。" :
        unavailableReason() ?? (HasInputErrors ? "请修正设置中标红的数字格式。" : options.ConfigurationIssue);
    public string AvailabilityMessage => StartBlockReason ?? "可开始：点击后复核已连接设备与实际焦位，再进行多焦位采样。";
    public bool CanStart => StartBlockReason is null;
    public string Status => status;
    public string EvidenceDirectory => result?.Directory ?? "";
    public SepMainFocusResult? Result => result;
    internal SepMainFocusOptions OptionsSnapshot => options with { };
    public IReadOnlyList<SepFocusPoint> Curve { get; private set; } = [];
    public int SelectedTabIndex { get => selectedTabIndex; set { selectedTabIndex = value; Notify(); } }
    public string SamplingSummary => $"共 {HalfPoints * 2 + 1} 个焦位 · 每处 {Frames} 张 × {ExposureSeconds:0.##} 秒；另有基准帧与候选往返复测。";
    public string CurveCaption { get; private set; } = "暂无本次曲线；开始采样后逐点更新。";
    public int MinimumPosition { get => options.MinimumPosition; set { options = options with { MinimumPosition = value }; ParameterChanged(); } }
    public int MaximumPosition { get => options.MaximumPosition; set { options = options with { MaximumPosition = value }; ParameterChanged(); } }
    public int Step { get => options.Step; set { options = options with { Step = value }; ParameterChanged(); } }
    public int HalfPoints { get => options.HalfPoints; set { options = options with { HalfPoints = value }; ParameterChanged(); } }
    public int Frames { get => options.Frames; set { options = options with { Frames = value }; ParameterChanged(); } }
    public double ExposureSeconds { get => options.ExposureMilliseconds / 1000d; set { if (!double.IsFinite(value) || value < 0 || value > 2147483) throw new ArgumentException("请输入有效曝光秒数。"); options = options with { ExposureMilliseconds = (int)Math.Round(value * 1000) }; ParameterChanged(); } }
    public int GainPercent { get => options.GainPercent; set { options = options with { GainPercent = value }; ParameterChanged(); } }
    public double Saturation { get => options.Saturation; set { options = options with { Saturation = value }; ParameterChanged(); } }
    public double MinimumAltitude { get => options.MinimumAltitude; set { options = options with { MinimumAltitude = value }; ParameterChanged(); } }
    public ICommand StartCommand => start;
    public ICommand CancelCommand => cancel;
    public ICommand SaveCommand => save;
    public ICommand OpenEvidenceCommand => open;

    internal void Refresh()
    {
        start.RaiseCanExecuteChanged(); save.RaiseCanExecuteChanged(); cancel.RaiseCanExecuteChanged(); open.RaiseCanExecuteChanged();
        Notify(nameof(AvailabilityMessage)); Notify(nameof(CanStart)); Notify(nameof(CanEdit));
    }
    private void ParameterChanged([System.Runtime.CompilerServices.CallerMemberName] string? property = null)
    { Notify(property); Notify(nameof(SamplingSummary)); Refresh(); }
    private void Notify([System.Runtime.CompilerServices.CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
    private void SetStatus(string text) { status = text; Notify(nameof(Status)); changed(); }
    private async Task StartAsync()
    {
        try { await ExecuteAsync(CancellationToken.None); }
        catch (Exception) { /* ExecuteAsync already publishes the specific reason next to the button. */ }
    }

    internal async Task<SepMainFocusResult> ExecuteAsync(CancellationToken token)
    {
        if (StartBlockReason is { } reason) { SetStatus("未启动：" + reason); throw new InvalidOperationException(reason); }
        var capturedProfile = profileId();
        var locked = options with { };
        settings.SepMainFocusOptionsJson = JsonSerializer.Serialize(locked);
        using var activeCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        cancellation = activeCancellation;
        result = null; Curve = []; ResetChart(); SelectedTabIndex = 1;
        CurveCaption = "本次采样 · 横轴：焦位（步），纵轴：R50（像素，越低越集中）。";
        Notify(nameof(CurveCaption)); Notify(nameof(Curve)); Notify(nameof(IsBusy)); Refresh(); changed();
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UVEX-ADV", "main-focus",
                $"{DateTime.UtcNow:yyyyMMddTHHmmss}-{Guid.NewGuid():N}");
            SetStatus("正在检查设备与焦位；不会连接相机、开顶或转向。检查通过后开始采样。");
            var watch = Stopwatch.StartNew();
            var completed = await host.RunFocusPreparationAsync(async () =>
            {
                await using var hardware = factory.CreateMainFocusHardware(settings, locked);
                return await new SepMainFocusRunner(hardware).RunAsync(locked, directory,
                    new Progress<string>(s => { if (cancellation == activeCancellation) SetStatus(s); }), activeCancellation.Token,
                    new Progress<SepFocusPoint[]>(p => { if (cancellation == activeCancellation) UpdateCurve(p); }));
            }, activeCancellation.Token);
            result = completed;
            AutoFocusDuration = watch.Elapsed;
            if (capturedProfile == profileId()) settings.SepMainFocusLastResultPath = Path.Combine(directory, "result.json");
            UpdateCurve(result.Curve);
            UpdateFinalMeasurement(result);
            SetStatus(result.Status == "Cancelled" ? "对焦已取消。" + (result.ReturnConfirmed ? $"已返回原焦位 {result.Origin}。" : result.Message) : result.Message);
            Notify(nameof(Result)); Notify(nameof(EvidenceDirectory));
            return completed;
        }
        catch (Exception ex) { SetStatus("主镜对焦未完成：" + ex.Message); throw; }
        finally
        {
            cancellation = null;
            Notify(nameof(IsBusy)); Refresh(); changed();
            if (reloadPending) { reloadPending = false; ReloadProfile(); }
        }
    }
    public void Dispose() => cancellation?.Cancel();
}
