using System.Windows.Input;

namespace UvexAdv.Nina.Plugin.UiHarness;

public sealed class NativeNightMock
{
    public bool StopAtDawn { get; set; } = true;
    public double DawnSunAltitudeDegrees { get; set; } = -12;
    public string EndAtIso8601 { get; set; } = "2026-09-21T05:00:00+08:00";
    public string NightState => "目标 2/3：WR 152 · 正在执行；目标间保持屋顶与镜盖状态。";
    public string JournalPath => "本夜记录：night-simulation.json（模拟示例）";
    public ICommand EndNightCommand => new NoOpCommand(true);
}
