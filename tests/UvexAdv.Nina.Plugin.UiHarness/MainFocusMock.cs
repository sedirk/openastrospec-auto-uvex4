using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using UvexAdv.Observatory;
using NINA.Core.Enum;
using NINA.Core.Utility;
using NINA.WPF.Base.Utility.AutoFocus;
using OxyPlot;
using OxyPlot.Series;
namespace UvexAdv.Nina.Plugin.UiHarness;
public sealed class MainFocusMock
{
    public bool IsBusy { get; set; }
    public bool CanEdit => !IsBusy;
    public int SelectedTabIndex { get; set; }
    public string AvailabilityMessage { get; set; } = "可开始：点击后复核已连接设备与实际焦位，再进行多焦位采样。";
    public bool CanStart { get; set; } = true;
    public int MinimumPosition { get;set; }=4600;
    public int MaximumPosition { get;set; }=5500;
    public int Step { get;set; }=50;
    public int HalfPoints { get;set; }=2;
    public double ExposureSeconds { get;set; }=10;
    public int Frames { get;set; }=3;
    public int GainPercent { get;set; }=100;
    public double Saturation { get;set; }=65520;
    public double MinimumAltitude { get;set; }=42;
    public string Status { get; set; } = "离线界面示例：候选焦点未显示重复优势，保留原位5000。不是当前设备状态。";
    public string SamplingSummary => "共 5 个焦位 · 每处 3 张 × 10 秒；另有基准帧与候选往返复测。";
    public string CurveCaption => "离线曲线示例 · 横轴焦位（步），纵轴 R50（像素）。不代表当前设备状态。";
    public AsyncObservableCollection<ScatterErrorPoint> FocusPoints { get; }
    public AsyncObservableCollection<DataPoint> PlotFocusPoints { get; }
    public DataPoint FinalFocusPoint => new(5000, 4.3);
    public ReportAutoFocusPoint LastAutoFocusPoint => new() { Focuspoint = FinalFocusPoint, Timestamp = new DateTime(2026, 9, 14), Temperature = 20, Filter = "PHD2 guide camera" };
    public HyperbolicFitting HyperbolicFitting { get; } = new();
    public TrendlineFitting TrendlineFitting { get; } = new();
    public QuadraticFitting QuadraticFitting { get; } = new();
    public GaussianFitting GaussianFitting { get; } = new();
    public AFMethodEnum AutoFocusChartMethod => AFMethodEnum.STARHFR;
    public AFCurveFittingEnum AutoFocusChartCurveFitting => AFCurveFittingEnum.HYPERBOLIC;
    public TimeSpan AutoFocusDuration => TimeSpan.FromMinutes(6);
    public MainFocusMock()
    {
        FocusPoints = new(Curve.Select(p => new ScatterErrorPoint(p.Position, p.R50, 0, p.Error)));
        PlotFocusPoints = new(Curve.Select(p => new DataPoint(p.Position, p.R50)));
        HyperbolicFitting.Calculate(FocusPoints); TrendlineFitting.Calculate(FocusPoints, "STARHFR");
    }
    public PointCollection CurvePoints => new([new Point(10,10),new Point(90,60),new Point(175,90),new Point(260,62),new Point(340,15)]);
    public SepFocusPoint[] Curve => [new(4900,6.2,10,.2,3),new(4950,4.8,8,.2,3),new(5000,4.3,7.8,.2,3),new(5050,4.9,8.1,.2,3),new(5100,6,10.2,.2,3)];
    public ICommand StartCommand => new NoOpCommand(CanStart && !IsBusy);
    public ICommand CancelCommand => new NoOpCommand(IsBusy);
    public ICommand SaveCommand => new NoOpCommand(true);
    public ICommand OpenEvidenceCommand => new NoOpCommand(true);
}
