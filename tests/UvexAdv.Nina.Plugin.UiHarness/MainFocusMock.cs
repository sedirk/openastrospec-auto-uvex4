using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using UvexAdv.Observatory;
namespace UvexAdv.Nina.Plugin.UiHarness;
public sealed class MainFocusMock
{
    public bool IsExpanded { get; set; } = true;
    public bool CanEdit => true;
    public int MinimumPosition { get;set; }=4600;
    public int MaximumPosition { get;set; }=5500;
    public int Step { get;set; }=50;
    public int HalfPoints { get;set; }=2;
    public int ExposureMilliseconds { get;set; }=10000;
    public int Frames { get;set; }=3;
    public int GainPercent { get;set; }=100;
    public double Saturation { get;set; }=65520;
    public double MinimumAltitude { get;set; }=42;
    public string Status => "离线界面示例：候选焦点未显示重复优势，保留原位5000。不是当前设备状态。";
    public PointCollection CurvePoints => new([new Point(10,10),new Point(90,60),new Point(175,90),new Point(260,62),new Point(340,15)]);
    public SepFocusPoint[] Curve => [new(4900,6.2,10,.2,3),new(4950,4.8,8,.2,3),new(5000,4.3,7.8,.2,3),new(5050,4.9,8.1,.2,3),new(5100,6,10.2,.2,3)];
    public ICommand StartCommand => new NoOpCommand(true);
    public ICommand CancelCommand => new NoOpCommand(false);
    public ICommand SaveCommand => new NoOpCommand(true);
    public ICommand OpenEvidenceCommand => new NoOpCommand(true);
}
