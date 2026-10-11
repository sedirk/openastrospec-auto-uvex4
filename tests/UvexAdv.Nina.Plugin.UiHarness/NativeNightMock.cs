using System.Windows.Input;

namespace UvexAdv.Nina.Plugin.UiHarness;

public sealed class NativeNightMock
{
    public bool EnableRealMode { get; set; } = true;
    public bool StartMonitoring { get; set; } = true;
    public bool CanEditPlan => true;
    public IReadOnlyList<UvexAdv.Nina.Plugin.SequenceItems.UvexTargetObservationContainer> Items { get; } =
        [NativeTargetEditorFixture.Create("Synthetic A"), NativeTargetEditorFixture.Create("Synthetic B", 120)];
    public bool StopAtDawn { get; set; } = true;
    public bool HomeBeforeParkOnFinalize { get; set; } = true;
    public int MountHomeTimeoutSeconds { get; set; } = 180;
    public bool HoldOpenAfterTargets { get; set; } = true;
    public double InitialGuideSettlePixels { get; set; } = 3;
    public int InitialGuideStableSeconds { get; set; } = 5;
    public int InitialGuideTimeoutSeconds { get; set; } = 180;
    public double DawnSunAltitudeDegrees { get; set; } = -12;
    public string EndAtIso8601 { get; set; } = "05:00";
    public string NightState { get; init; } = "目标 2/3：WR 152 · 正在执行；目标间保持屋顶与镜盖状态。";
    public string JournalPath => "本夜记录：night-simulation.json（模拟示例）";
    public ICommand EndNightCommand => new NoOpCommand(true);
}
