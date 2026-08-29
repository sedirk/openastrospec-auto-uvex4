using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class CommissioningSchemaSourceSafetyTests
{
    private static readonly string PresetSource = File.ReadAllText(Path.Combine(
        AppContext.BaseDirectory,
        "Sources",
        "RealCommissioningPreset.cs"));
    private static readonly string DockableSource = File.ReadAllText(Path.Combine(
        AppContext.BaseDirectory,
        "Sources",
        "ObservationDockable.cs"));

    [Fact]
    public void AutomaticRealScienceRequiresSchemaFivePhd2AndOpticalSlitIdentityForEveryAuthority()
    {
        Assert.Contains("if (preset.SchemaVersion != 5)", PresetSource, StringComparison.Ordinal);
        Assert.Contains("if (preset.Phd2SlitPlacement is null)", PresetSource, StringComparison.Ordinal);
        Assert.Contains("issues.AddRange(preset.Phd2SlitPlacement.Validate())", PresetSource, StringComparison.Ordinal);
        Assert.Contains("if (preset.SlitWheelIdentity is null)", PresetSource, StringComparison.Ordinal);
        Assert.Contains("issues.AddRange(preset.SlitWheelIdentity.Validate())", PresetSource, StringComparison.Ordinal);
        var schemaGate = PresetSource.IndexOf("if (preset.SchemaVersion != 5)", StringComparison.Ordinal);
        var authorityBranch = PresetSource.IndexOf("if (preset.FineMotionAuthority is", schemaGate, StringComparison.Ordinal);
        var phdValidation = PresetSource.IndexOf("issues.AddRange(preset.Phd2SlitPlacement.Validate())", schemaGate, StringComparison.Ordinal);
        Assert.True(schemaGate >= 0 && phdValidation > schemaGate && authorityBranch > phdValidation);
    }

    [Fact]
    public void UiHashesActualPresetBytesBeforeParsingOrDisplayingPolicy()
    {
        var start = DockableSource.IndexOf("private (string Policy, string Route) ReadPhd2CommissioningSummary()", StringComparison.Ordinal);
        var end = DockableSource.IndexOf("private string Permission(", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var body = DockableSource[start..end];
        var read = body.IndexOf("File.ReadAllBytes", StringComparison.Ordinal);
        var hash = body.IndexOf("SHA256.HashData(bytes)", StringComparison.Ordinal);
        var expected = body.IndexOf("settings.CommissioningPresetSha256", StringComparison.Ordinal);
        var mismatchReturn = body.IndexOf("preset SHA-256 未验证", StringComparison.Ordinal);
        var parse = body.IndexOf("JsonDocument.Parse(bytes)", StringComparison.Ordinal);
        Assert.True(read >= 0 && hash > read && expected > hash && mismatchReturn > expected && parse > mismatchReturn);
    }
}
