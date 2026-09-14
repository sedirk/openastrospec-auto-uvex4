using System.ComponentModel;
using System.ComponentModel.Composition;
using System.IO;
using System.Windows;
using NINA.Core.Enum;
using NINA.Core.Interfaces;
using NINA.Core.Model;
using NINA.Core.Model.Equipment;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using NINA.Equipment.Equipment.MyFilterWheel;
using NINA.WPF.Base.Interfaces;
using NINA.WPF.Base.Interfaces.ViewModel;
using NINA.WPF.Base.Utility.AutoFocus;
using OxyPlot;
using OxyPlot.Series;
using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

/// <summary>Opt-in native autofocus behavior. Does not replace the master camera or run NINA's camera-based AutoFocusVM.</summary>
[Export(typeof(IPluggableBehavior))]
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class SepMainFocusFactory : IAutoFocusVMFactory
{
    private readonly Lazy<ObservationDockable> observation;
    [ImportingConstructor]
    public SepMainFocusFactory(Lazy<ObservationDockable> observation) => this.observation = observation;
    public string Name => ObservationUiPresentation.Text("OpenAstroSpec SEP · 主镜 / PHD2", "OpenAstroSpec SEP · Main mirror / PHD2");
    public string ContentId => typeof(SepMainFocusFactory).FullName!;
    public IAutoFocusVM Create()
    {
        var dispatcher = Application.Current?.Dispatcher;
        return dispatcher is not null && !dispatcher.CheckAccess()
            ? dispatcher.Invoke(() => observation.Value.MainFocus) : observation.Value.MainFocus;
    }
}

public sealed partial class SepMainFocusViewModel : IAutoFocusVM
{
    private AsyncObservableCollection<ScatterErrorPoint> focusPoints = new();
    private AsyncObservableCollection<DataPoint> plotFocusPoints = new();
    private DataPoint finalFocusPoint = DataPoint.Undefined;
    private ReportAutoFocusPoint? lastAutoFocusPoint;
    private HyperbolicFitting hyperbolicFitting = new();
    private TrendlineFitting trendlineFitting = new();
    private QuadraticFitting quadraticFitting = new();
    private GaussianFitting gaussianFitting = new();
    private AFMethodEnum chartMethod = AFMethodEnum.STARHFR;
    private AFCurveFittingEnum chartFitting = AFCurveFittingEnum.HYPERBOLIC;
    private TimeSpan duration;
    public AsyncObservableCollection<ScatterErrorPoint> FocusPoints { get => focusPoints; set { focusPoints = value; Notify(); } }
    public AsyncObservableCollection<DataPoint> PlotFocusPoints { get => plotFocusPoints; set { plotFocusPoints = value; Notify(); } }
    public DataPoint FinalFocusPoint { get => finalFocusPoint; set { finalFocusPoint = value; Notify(); } }
    public ReportAutoFocusPoint LastAutoFocusPoint { get => lastAutoFocusPoint!; set { lastAutoFocusPoint = value; Notify(); } }
    public HyperbolicFitting HyperbolicFitting { get => hyperbolicFitting; set { hyperbolicFitting = value; Notify(); } }
    public TrendlineFitting TrendlineFitting { get => trendlineFitting; set { trendlineFitting = value; Notify(); } }
    public QuadraticFitting QuadraticFitting { get => quadraticFitting; set { quadraticFitting = value; Notify(); } }
    public GaussianFitting GaussianFitting { get => gaussianFitting; set { gaussianFitting = value; Notify(); } }
    public AFMethodEnum AutoFocusChartMethod { get => chartMethod; set { chartMethod = value; Notify(); } }
    public AFCurveFittingEnum AutoFocusChartCurveFitting { get => chartFitting; set { chartFitting = value; Notify(); } }
    public TimeSpan AutoFocusDuration { get => duration; set { duration = value; Notify(); } }

    private void ResetChart()
    {
        Curve = []; FocusPoints = new(); PlotFocusPoints = new(); FinalFocusPoint = DataPoint.Undefined;
        LastAutoFocusPoint = null!; HyperbolicFitting = new(); TrendlineFitting = new();
        QuadraticFitting = new(); GaussianFitting = new(); AutoFocusDuration = TimeSpan.Zero;
        CurveCaption = "尚无本次曲线。历史记录不代表当前焦位。";
    }
    private void UpdateCurve(SepFocusPoint[] points)
    {
        Curve = points;
        FocusPoints = new(points.OrderBy(p => p.Position).Select(p => new ScatterErrorPoint(p.Position, p.R50, 0, p.Error)));
        PlotFocusPoints = new(points.OrderBy(p => p.Position).Select(p => new DataPoint(p.Position, p.R50)));
        SetCurveFittings("STARHFR", "HYPERBOLIC");
        Notify(nameof(Curve));
    }
    private void UpdateFinalMeasurement(SepMainFocusResult completed)
    {
        if (!completed.ReturnConfirmed || completed.FinalPosition is null) return;
        // Display the latest measured group at the retained position, never an inferred
        // curve minimum or default zero as if it were a final measurement.
        var group = completed.Samples.Where(s => s.Position == completed.FinalPosition.Value)
            .GroupBy(s => s.Group).OrderByDescending(g => g.Key).FirstOrDefault();
        if (group is null) return;
        try
        {
            var point = SepMainFocusRunner.Summarize(group.ToArray()).Single();
            FinalFocusPoint = new(point.Position, point.R50);
            LastAutoFocusPoint = new() { Focuspoint = FinalFocusPoint, Timestamp = DateTime.Now, Temperature = double.NaN, Filter = "PHD2 guide camera" };
        }
        catch (InvalidOperationException) { /* The status/record retains the failed measurement; do not manufacture one. */ }
    }
    public void SetCurveFittings(string method, string fitting)
    {
        // Historical charts can request the native fitting variants. This only draws curves, never moves a device.
        AutoFocusChartMethod = Enum.TryParse<AFMethodEnum>(method, out var m) ? m : AFMethodEnum.STARHFR;
        AutoFocusChartCurveFitting = Enum.TryParse<AFCurveFittingEnum>(fitting, out var f) ? f : AFCurveFittingEnum.HYPERBOLIC;
        HyperbolicFitting = new(); TrendlineFitting = new(); QuadraticFitting = new(); GaussianFitting = new();
        if (FocusPoints.Select(p => p.X).Distinct().Count() < 3) return;
        try
        {
            TrendlineFitting = new TrendlineFitting().Calculate(FocusPoints, method);
            if (AutoFocusChartMethod == AFMethodEnum.STARHFR)
            {
                HyperbolicFitting = new HyperbolicFitting().Calculate(FocusPoints);
                if (AutoFocusChartCurveFitting is AFCurveFittingEnum.PARABOLIC or AFCurveFittingEnum.TRENDPARABOLIC)
                    QuadraticFitting = new QuadraticFitting().Calculate(FocusPoints);
            }
            else GaussianFitting = new GaussianFitting().Calculate(FocusPoints);
        }
        catch (Exception) { /* Partial/degenerate points remain visible; the shared runner alone decides the focus result. */ }
    }

    public Task<AutoFocusReport> StartAutoFocus(FilterInfo filter, CancellationToken token, IProgress<ApplicationStatus> progress)
    {
        var dispatcher = Application.Current?.Dispatcher;
        return dispatcher is not null && !dispatcher.CheckAccess()
            ? dispatcher.InvokeAsync(() => StartNativeAsync(token, progress)).Task.Unwrap() : StartNativeAsync(token, progress);
    }
    private async Task<AutoFocusReport> StartNativeAsync(CancellationToken token, IProgress<ApplicationStatus> progress)
    {
        var capturedProfile = profileId();
        void Report(object? sender, PropertyChangedEventArgs e)
        { if (e.PropertyName == nameof(Status)) progress?.Report(new() { Status = Status }); }
        PropertyChanged += Report;
        try
        {
            var completed = await ExecuteAsync(token);
            // Mere retention is not success; verified near-optimal origin has
            // independent repeated and final-return optical evidence.
            if (!completed.FocusVerified || capturedProfile != profileId()) return null!;
            if (lastAutoFocusPoint is null || !double.IsFinite(FinalFocusPoint.Y)) return null!;
            var report = new AutoFocusReport
            {
                AutoFocuserName = "OpenAstroSpec SEP / PHD2", StarDetectorName = "SEP fixed-aperture R50/R80",
                Timestamp = LastAutoFocusPoint.Timestamp, Temperature = double.NaN, Filter = "PHD2 guide camera",
                Method = "STARHFR", Fitting = "HYPERBOLIC", Duration = AutoFocusDuration,
                InitialFocusPoint = new() { Position = completed.Origin,
                    Value = SepMainFocusRunner.Summarize(completed.Samples.Where(s => s.Group == 0).ToArray()).Single().R50 },
                CalculatedFocusPoint = new() { Position = FinalFocusPoint.X, Value = FinalFocusPoint.Y },
                MeasurePoints = completed.Curve.Select(p => new FocusPoint { Position = p.Position, Value = p.R50, Error = p.Error }).ToArray(),
            };
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NINA", "AutoFocus");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}_{capturedProfile}_SEP_{Guid.NewGuid():N}.json");
            var pending = path + ".pending";
            // NINA watches *.json: publish only after the complete report is flushed.
            await using (var stream = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await using var writer = new StreamWriter(stream, leaveOpen: true);
                await writer.WriteAsync(Newtonsoft.Json.JsonConvert.SerializeObject(report, Newtonsoft.Json.Formatting.Indented));
                await writer.FlushAsync();
                stream.Flush(true);
            }
            File.Move(pending, path); // Unique path, no overwrite of any observation or historical report.
            return report;
        }
        catch (OperationCanceledException) { return null!; }
        catch (Exception ex) { Notification.ShowError("主镜对焦未完成：" + ex.Message); return null!; }
        finally { PropertyChanged -= Report; progress?.Report(new() { Status = Status }); }
    }
}
