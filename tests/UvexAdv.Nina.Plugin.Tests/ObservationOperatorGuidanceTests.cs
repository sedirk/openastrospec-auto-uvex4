using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class ObservationOperatorGuidanceTests
{
    [Fact]
    public void FocusFailurePointsToG3AndCorrectPhysicalFocuser()
    {
        var guidance = ObservationOperatorGuidance.For(
            ObservationStage.AcquireG3SlitField,
            GateResult.Unknown("G3_MAIN_FOCUS_UNVERIFIED", "broad stars"));

        Assert.Equal(ObservationPreviewChannel.G3SlitField, guidance.PreviewChannel);
        Assert.Contains("Star Focuser Pro", guidance.Recommendation, StringComparison.Ordinal);
        Assert.Contains("Gemini", guidance.Recommendation, StringComparison.Ordinal);
        Assert.Contains("UVEX M2", guidance.Recommendation, StringComparison.Ordinal);
        Assert.Contains("ToupTek AAF", guidance.Recommendation, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ObservationStage.AcquireQhyWideField, "QHY_LOW_STAR_COUNT", ObservationPreviewChannel.QhyWideField)]
    [InlineData(ObservationStage.CoarseCenter, "PLATE_SOLVE_FAILED", ObservationPreviewChannel.QhyWideField)]
    [InlineData(ObservationStage.PlaceTargetOnSlit, "SLIT_TARGET_RESIDUAL", ObservationPreviewChannel.G3SlitField)]
    [InlineData(ObservationStage.StartGuiding, "PHD_SETTLE_FAILED", ObservationPreviewChannel.G3SlitField)]
    [InlineData(ObservationStage.RunScienceBlock, "ATR_SATURATED", ObservationPreviewChannel.AtrSpectrum)]
    public void ImageFailuresPointToTheirOwningPreview(
        ObservationStage stage,
        string code,
        ObservationPreviewChannel expected)
    {
        var guidance = ObservationOperatorGuidance.For(stage, GateResult.Fail(code, "synthetic"));

        Assert.Equal(expected, guidance.PreviewChannel);
        Assert.False(string.IsNullOrWhiteSpace(guidance.Recommendation));
    }

    [Fact]
    public void IdentityFailureDoesNotPretendThereIsAnAstronomicalImage()
    {
        var guidance = ObservationOperatorGuidance.For(
            ObservationStage.ValidateNightSetup,
            GateResult.Unknown("NINA_PROFILE_OWNER_MISMATCH", "wrong device"));

        Assert.Null(guidance.PreviewChannel);
        Assert.Contains("Night Setup", guidance.Recommendation, StringComparison.Ordinal);
        Assert.Contains("设备身份", guidance.Recommendation, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SLIT_RETURN_COMMAND_NOT_REACHED")]
    [InlineData("SLIT_BUDGET_MULTIPLE_ACTIVE_LINEAGES")]
    public void DurableSlitRecoveryFailureExplainsResumeAndTakeoverWithoutFileBypass(string code)
    {
        var guidance = ObservationOperatorGuidance.For(
            ObservationStage.PlaceTargetOnSlit,
            GateResult.Unknown(code, "synthetic durable recovery failure"));

        Assert.Equal(ObservationPreviewChannel.G3SlitField, guidance.PreviewChannel);
        Assert.Contains("实报位置", guidance.Recommendation, StringComparison.Ordinal);
        Assert.Contains("恢复", guidance.Recommendation, StringComparison.Ordinal);
        Assert.Contains("人工接管", guidance.Recommendation, StringComparison.Ordinal);
        Assert.Contains("不要删除", guidance.Recommendation, StringComparison.Ordinal);
    }

    [Fact]
    public void DashboardPublishesEvidenceImmediatelyForTheOperator()
    {
        using var host = new ObservationCoordinatorHost();
        var published = 0;
        host.DashboardChanged += (_, _) => published++;
        var path = Path.GetFullPath(Path.Combine("evidence", "synthetic-focus.json"));

        host.PublishEvidence("g3-focus-analysis", path, metadata: new Dictionary<string, string>
        {
            ["gateCode"] = "G3_MAIN_FOCUS_UNVERIFIED",
        });

        var evidence = Assert.Single(host.Dashboard.Evidence);
        Assert.Equal("g3-focus-analysis", evidence.Kind);
        Assert.Equal(path, evidence.AbsolutePath);
        Assert.Equal("G3_MAIN_FOCUS_UNVERIFIED", evidence.Metadata!["gateCode"]);
        Assert.Equal(1, published);
    }
}
