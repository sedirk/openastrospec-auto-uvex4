using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class WeakSupervisionWeatherPolicyTests
{
    private static readonly string RunnerSource = File.ReadAllText(Path.Combine(
        FindRepositoryRoot(),
        "src",
        "UvexAdv.Nina.Plugin",
        "RealObservationStageRunner.cs"));

    [Fact]
    public void HighHumidityIsAdvisoryInEverySupervisionMode()
    {
        Assert.Contains("environmentWarnings.Add($\"high humidity", RunnerSource, StringComparison.Ordinal);
        Assert.DoesNotContain("GateResult.Fail(\"HUMIDITY_LIMIT\"", RunnerSource, StringComparison.Ordinal);
        Assert.Contains("Humidity alone is not evidence of rain or an unsafe roof", RunnerSource, StringComparison.Ordinal);
    }

    [Fact]
    public void MeasuredRainAndHighWindRemainHardStops()
    {
        Assert.Contains("GateResult.Fail(\"RAIN_DETECTED\"", RunnerSource, StringComparison.Ordinal);
        Assert.Contains("GateResult.Fail(\"WIND_LIMIT\"", RunnerSource, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "UVEX-ADV.sln"))) return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Repository root containing UVEX-ADV.sln was not found.");
    }
}
