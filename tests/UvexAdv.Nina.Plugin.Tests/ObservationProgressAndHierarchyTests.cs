using System.Text.RegularExpressions;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class ObservationProgressAndHierarchyTests
{
    private static readonly string Root = FindRepositoryRoot();
    private static readonly string Dockable = File.ReadAllText(Path.Combine(
        Root, "src", "UvexAdv.Nina.Plugin", "ObservationDockable.cs"));
    private static readonly string Xaml = File.ReadAllText(Path.Combine(
        Root, "src", "UvexAdv.Nina.Plugin", "Templates.xaml"));

    [Fact]
    public void SimulationAndRealRunnerProgressReachTheDock()
    {
        Assert.Equal(
            2,
            Regex.Matches(
                Dockable,
                "new Progress<ApplicationStatus>\\(ApplyApplicationStatus\\)",
                RegexOptions.CultureInvariant).Count);
        Assert.Contains("CurrentOperationPercent = Math.Clamp(fraction, 0, 1) * 100d", Dockable, StringComparison.Ordinal);
        Assert.Contains("double.IsFinite(fraction) && fraction >= 0", Dockable, StringComparison.Ordinal);
    }

    [Fact]
    public void FixedRunBarShowsOverallAndCurrentOperationProgressSeparately()
    {
        Assert.Contains("Text=\"{Binding ProgressSummary}\"", Xaml, StringComparison.Ordinal);
        Assert.Contains("Value=\"{Binding ProgressPercent, Mode=OneWay}\"", Xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding CurrentOperationText, StringFormat=当前操作：{0}}\"", Xaml, StringComparison.Ordinal);
        Assert.Contains("Value=\"{Binding CurrentOperationPercent, Mode=OneWay}\"", Xaml, StringComparison.Ordinal);
        Assert.Contains("Visibility=\"{Binding HasCurrentOperationProgress", Xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void DiagnosticsAreOneWorkspaceAndLocalActionErrorIsNotRunBlocker()
    {
        Assert.Single(Regex.Matches(Xaml, "Header=\"诊断与证据\"", RegexOptions.CultureInvariant).Cast<Match>());
        Assert.DoesNotContain("Header=\"失败诊断\"", Xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Header=\"质量门与证据\"", Xaml, StringComparison.Ordinal);
        Assert.Contains("Header=\"当前问题\"", Xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"当前影响\"", Xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"自动处理\"", Xaml, StringComparison.Ordinal);
        Assert.Contains("Header=\"技术详情（原始消息）\"", Xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"本次界面操作未完成\"", Xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"错误 / 阻断\"", Xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void AdvancedSettingsSeparatesRoutineAndEngineeringSections()
    {
        Assert.Contains("Text=\"运行准入与数据归档\"", Xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"目标获取、入缝与导星\"", Xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"并行观测与站点\"", Xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"设备身份与标定证据\"", Xaml, StringComparison.Ordinal);
        Assert.Contains("Header=\"目标获取算法与恢复预算（工程参数）\"", Xaml, StringComparison.Ordinal);
        Assert.Contains("Header=\"设备身份、哈希与路径（工程核对）\"", Xaml, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(Xaml, "Text=\"\\{Binding GhostCalibrationSummaryText\\}\"", RegexOptions.CultureInvariant).Cast<Match>());
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src")) &&
                File.Exists(Path.Combine(directory.FullName, "UVEX-ADV.sln")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
