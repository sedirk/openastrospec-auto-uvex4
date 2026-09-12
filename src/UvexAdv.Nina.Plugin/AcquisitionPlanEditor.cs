using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;

namespace UvexAdv.Nina.Plugin;

public sealed record AcquisitionPlanValues(
    int ScienceFrames, int MaximumAttempts, string ExposureLadderSeconds,
    double ProbeSeconds, double PlanningMinutes)
{
    internal static AcquisitionPlanValues Read(UvexPluginSettings settings) => new(
        settings.AtrScienceFrameCount, settings.AtrScienceMaximumAttempts,
        settings.AtrExposureLadderSecondsCsv, settings.AtrProbeExposureSeconds,
        settings.ObservationDurationMinutes);

    internal void Apply(UvexPluginSettings settings)
    {
        settings.AtrScienceFrameCount = ScienceFrames;
        settings.AtrScienceMaximumAttempts = MaximumAttempts;
        settings.AtrExposureLadderSecondsCsv = ExposureLadderSeconds;
        settings.AtrProbeExposureSeconds = ProbeSeconds;
        settings.ObservationDurationMinutes = PlanningMinutes;
    }
}

/// <summary>A settings-only draft. Saving never creates a runner or touches a device.</summary>
public sealed class AcquisitionPlanEditor : INotifyPropertyChanged
{
    private readonly Func<AcquisitionPlanValues> read;
    private readonly Action<AcquisitionPlanValues> save;
    private readonly Func<bool> canEdit;
    private readonly Func<CultureInfo> culture;
    private readonly SimpleCommand saveCommand;
    private readonly SimpleCommand reloadCommand;
    private string frames = "", attempts = "", ladder = "", probe = "", minutes = "";
    private string saveError = "";
    private AcquisitionPlanValues saved = null!;

    public AcquisitionPlanEditor(Func<AcquisitionPlanValues> read, Action<AcquisitionPlanValues> save,
        Func<bool> canEdit, Func<CultureInfo> culture)
    {
        this.read = read;
        this.save = save;
        this.canEdit = canEdit;
        this.culture = culture;
        saveCommand = new SimpleCommand(Save, () => IsEditable && HasPendingChanges && TryValidate(out _, out _));
        reloadCommand = new SimpleCommand(Reload, () => IsEditable);
        Reload();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ICommand SaveCommand => saveCommand;
    public ICommand ReloadCommand => reloadCommand;
    public bool IsEditable => canEdit();
    public string ScienceFrames { get => frames; set => Edit(ref frames, value); }
    public string MaximumAttempts { get => attempts; set => Edit(ref attempts, value); }
    public string ExposureLadderSeconds { get => ladder; set => Edit(ref ladder, value); }
    public string ProbeSeconds { get => probe; set => Edit(ref probe, value); }
    public string PlanningMinutes { get => minutes; set => Edit(ref minutes, value); }
    public bool HasPendingChanges => frames != Number(saved.ScienceFrames) || attempts != Number(saved.MaximumAttempts) ||
        ladder != saved.ExposureLadderSeconds || probe != Number(saved.ProbeSeconds) || minutes != Number(saved.PlanningMinutes);
    public string ValidationMessage => TryValidate(out _, out var error) ? "" : error;
    public bool HasValidationError => !string.IsNullOrEmpty(ValidationMessage);
    public string StateText => !IsEditable
        ? Text("运行已锁定；结束或取消后才能修改下一轮计划。", "Run locked; edit the next plan after completion or cancellation.")
        : !string.IsNullOrEmpty(saveError)
            ? Text($"保存失败，仍使用原计划：{saveError}", $"Save failed; the previous plan remains active: {saveError}")
            : HasPendingChanges
                ? Text("草稿尚未保存：请保存或还原后再启动。", "Unsaved draft: save or restore it before starting.")
                : Text("当前配置已载入；保存的计划将在下次启动时使用，重启后保留。", "Current profile loaded; saved plans apply on the next start and persist across restarts.");
    public string SavedSummary => Text(
        $"当前生效：{saved.ScienceFrames} 张合格光谱 · 最多 {saved.MaximumAttempts} 次正式尝试 · 自动选档",
        $"Applied: {saved.ScienceFrames} accepted spectra · up to {saved.MaximumAttempts} science attempts · automatic tier selection");

    public string BudgetSummary
    {
        get
        {
            if (!TryValidate(out var value, out _)) return Text("修正输入后显示曝光预算。", "Correct the inputs to display the exposure budget.");
            var tiers = ParseLadder(value!.ExposureLadderSeconds);
            return Text(
                $"合格科学累计：{Duration(tiers.Min() * value.ScienceFrames)} ～ {Duration(tiers.Max() * value.ScienceFrames)}；实际为合格张数 × 选中档位。\n正式尝试曝光上界：{Duration(tiers.Max() * value.MaximumAttempts)}（含可能不合格的正式帧，不含试拍、读出、定位和恢复）。",
                $"Accepted science integration: {Duration(tiers.Min() * value.ScienceFrames)} – {Duration(tiers.Max() * value.ScienceFrames)}; actual total is accepted frames × selected tier.\nScience-attempt exposure ceiling: {Duration(tiers.Max() * value.MaximumAttempts)} (includes rejected science attempts; excludes probes, readout, acquisition and recovery).");
        }
    }

    public string PlanningWarning
    {
        get
        {
            if (!TryValidate(out var value, out _)) return "";
            var maximumSeconds = ParseLadder(value!.ExposureLadderSeconds).Max() * value.MaximumAttempts;
            return value.PlanningMinutes * 60 <= maximumSeconds
                ? Text("规划窗口不大于最长档正式尝试曝光上界；请为定位、试拍和恢复留出余量。它不是曝光硬截止。",
                    "The planning window does not exceed the longest-tier science-attempt ceiling; allow extra time for acquisition, probes and recovery. It is not an exposure cutoff.")
                : "";
        }
    }

    public void Reload()
    {
        saved = read();
        frames = Number(saved.ScienceFrames);
        attempts = Number(saved.MaximumAttempts);
        ladder = saved.ExposureLadderSeconds;
        probe = Number(saved.ProbeSeconds);
        minutes = Number(saved.PlanningMinutes);
        saveError = "";
        NotifyState();
    }

    public void NotifyState()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        saveCommand.RaiseCanExecuteChanged();
        reloadCommand.RaiseCanExecuteChanged();
    }

    public bool TryValidate(out AcquisitionPlanValues? value, out string error)
    {
        value = null;
        error = "";
        if (!int.TryParse(frames, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) || count <= 0)
            error = Text("合格科学帧数必须是正整数。", "Accepted science frames must be a positive integer.");
        else if (!int.TryParse(attempts, NumberStyles.Integer, CultureInfo.InvariantCulture, out var maximum) || maximum < count)
            error = Text("最大正式尝试次数必须是整数，且不能少于合格帧数。", "Maximum science attempts must be an integer at least as large as the accepted frame count.");
        else if (!Positive(probe, out var probeValue))
            error = Text("起始试拍曝光必须是大于零的有限秒数。", "Initial probe exposure must be a positive finite number of seconds.");
        else if (!Positive(minutes, out var minuteValue) || minuteValue >= TimeSpan.MaxValue.TotalMinutes)
            error = Text("规划窗口必须是有效的正分钟数。", "The planning window must be a valid positive number of minutes.");
        else
        {
            var parts = ladder.Replace('，', ',').Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length == 0 || parts.Any(part => !Positive(part, out _)))
                error = Text("曝光档位请用逗号分隔正秒数；不能含空档、零、负数或非数字。", "Separate positive exposure seconds with commas; empty tiers, zero, negative or non-numeric entries are invalid.");
            else
            {
                var tiers = parts.Select(part => double.Parse(part, CultureInfo.InvariantCulture)).Distinct().Order().ToArray();
                if (!tiers.Any(tier => Math.Abs(tier - probeValue) < 1e-9))
                    error = Text("起始试拍曝光必须包含在自动曝光档位中。", "The initial probe exposure must be included in the exposure ladder.");
                else if (!double.IsFinite(tiers.Max() * maximum))
                    error = Text("曝光预算过大，无法表示；请缩小档位或尝试次数。", "The exposure budget is too large to represent; reduce tiers or attempts.");
                else
                    value = new(count, maximum, string.Join(',', tiers.Select(Number)), probeValue, minuteValue);
            }
        }
        return value is not null;
    }

    private void Save()
    {
        if (!IsEditable || !TryValidate(out var value, out _)) return;
        try { save(value!); Reload(); }
        catch (Exception ex) { saveError = ex.Message; NotifyState(); }
    }

    private void Edit(ref string field, string? value)
    {
        if (!IsEditable || field == value) return;
        field = value ?? "";
        saveError = "";
        NotifyState();
    }

    private static bool Positive(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value) && value > 0;
    private static double[] ParseLadder(string text) => text.Split(',').Select(part => double.Parse(part, CultureInfo.InvariantCulture)).ToArray();
    private static string Number(double value) => value.ToString("G", CultureInfo.InvariantCulture);
    private string Text(string zh, string en) => ObservationUiPresentation.Text(zh, en, culture());
    private string Duration(double seconds) => seconds >= 3600
        ? Text($"{seconds / 3600:G4} 小时", $"{seconds / 3600:G4} h")
        : seconds >= 60 ? Text($"{seconds / 60:G4} 分钟", $"{seconds / 60:G4} min") : Text($"{seconds:G4} 秒", $"{seconds:G4} s");
}
