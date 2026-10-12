using NINA.Core.Model;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class ObservationStatusProgressTests
{
    [Fact]
    public void SingleBarEntryKeepsProgressAndDoesNotMutateOriginalDetails()
    {
        var reports = new Capture();
        var progress = ObservationStatusProgress.Wrap(reports);
        Assert.Same(progress, ObservationStatusProgress.Wrap(progress));
        var original = new ApplicationStatus
        {
            Source = "OpenAstroSpec Auto（真实）",
            Status = "光谱相机监督试拍警告：实测入缝残差 2.05, 2.06, 1.78 px；保留全部测量，按实际光谱信号评估，不声明精确入缝。",
            Progress = .5, MaxProgress = 6, Status2 = "曝光", Progress2 = .25,
            ProgressType = ApplicationStatus.StatusProgressType.ValueOfMaxValue
        };
        progress.Report(original);
        Assert.Equal("入缝偏差 ≤2.06 px · 警告试拍", reports.Last!.Status);
        Assert.Contains("保留全部测量", original.Status);
        Assert.Equal(.5, reports.Last.Progress);
        Assert.Equal(6, reports.Last.MaxProgress);
        Assert.Equal(original.ProgressType, reports.Last.ProgressType);
        Assert.Equal(.25, reports.Last.Progress2);
        progress.Report(new() { Source = "OpenAstroSpec Auto", Status = "光谱相机科学曝光与健康监测" });
        Assert.Equal(ObservationStatusProgress.Source, reports.Last.Source);
        progress.Report(new() { Source = "OpenAstroSpec 启动", Status = "真实模式已选择" });
        Assert.Equal(ObservationStatusProgress.Source, reports.Last.Source);
        var native = new ApplicationStatus { Source = "Camera", Status = "native owner" };
        progress.Report(native);
        Assert.Same(native, reports.Last);
        progress.Report(new() { Source = "OpenAstroSpec Auto（模拟）", Status = "曝光" });
        Assert.StartsWith("SIM ·", reports.Last!.Status);
        progress.Report(new() { Source = "OpenAstroSpec Auto", Status = "" });
        Assert.Empty(reports.Last!.Status);
    }

    [Theory]
    [InlineData("Unsafe: rain detected; roof closing", "Unsafe: rain detected")]
    [InlineData("取消：等待设备停止；未确认停止", "取消：等待设备停止")]
    [InlineData(null, "")]
    public void StatusPrefixAndEmptyClearArePreserved(string? original, string expected) =>
        Assert.Equal(expected, ObservationStatusProgress.Compact(original));

    [Fact]
    public void LongStatusIsBoundedAndExplicitlyEllipsized()
    {
        var result = ObservationStatusProgress.Compact(new string('测', 200));
        Assert.EndsWith("…", result);
        Assert.InRange(result.Length, 1, 27);
    }

    private sealed class Capture : IProgress<ApplicationStatus>
    {
        public ApplicationStatus? Last { get; private set; }
        public void Report(ApplicationStatus value) => Last = value;
    }
}
