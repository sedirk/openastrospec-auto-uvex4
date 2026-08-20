using System.IO;
using UvexAdv.Nina.Plugin.UiHarness;

namespace UvexAdv.Nina.Plugin.UiHarness.Tests;

public sealed class HarnessOptionsTests
{
    [Fact]
    public void Parse_RejectsImplicitRendering()
    {
        var accepted = HarnessOptions.TryParse([], out _, out var error);

        Assert.False(accepted);
        Assert.Contains("--render", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_AcceptsExplicitRenderAndScenario()
    {
        var accepted = HarnessOptions.TryParse(
            ["--render", "--scenario", "failure", "--output", ".\tmp\visual-test"],
            out var options,
            out var error);

        Assert.True(accepted, error);
        Assert.True(options.Render);
        Assert.Equal("failure", options.Scenario);
        Assert.True(Path.IsPathFullyQualified(options.OutputDirectory));
    }

    [Fact]
    public void Parse_RejectsUnknownScenario()
    {
        var accepted = HarnessOptions.TryParse(
            ["--render", "--scenario", "hardware"],
            out _,
            out var error);

        Assert.False(accepted);
        Assert.Contains("Unknown scenario", error, StringComparison.Ordinal);
    }
}
