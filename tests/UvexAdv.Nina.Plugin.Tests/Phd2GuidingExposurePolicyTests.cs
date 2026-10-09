using System.Globalization;
using UvexAdv.Nina.Plugin;
using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class Phd2GuidingExposurePolicyTests
{
    [Theory]
    [InlineData(2, 2000, true)]
    [InlineData(1.96, 2000, true)]
    [InlineData(2.04, 2000, true)]
    [InlineData(1.959, 2000, false)]
    [InlineData(2.041, 2000, false)]
    [InlineData(1.5, 2000, false)]
    [InlineData(3, 2000, false)]
    [InlineData(1.5, 1500, true)]
    [InlineData(0, 2000, false)]
    [InlineData(-2, 2000, false)]
    [InlineData(double.NaN, 2000, false)]
    [InlineData(double.PositiveInfinity, 2000, false)]
    [InlineData(double.MaxValue, 2000, false)]
    [InlineData(2, 0, false)]
    public void RetainsExactCommissionedExposureAndOriginalTolerance(double seconds, int expected, bool passed)
    {
        var gate = Phd2GuidingExposurePolicy.Evaluate(seconds, expected, "OffSlitGuideStar");
        Assert.Equal(passed, gate.Disposition == GateDisposition.Passed);
        Assert.Equal(expected, gate.Metrics!["expectedGuideExposureMilliseconds"]);
        Assert.All(gate.Metrics.Values, value => Assert.True(double.IsFinite(value)));
        if (!passed) Assert.Equal(Phd2GuidingExposurePolicy.MismatchCode, gate.Code);
    }

    [Theory]
    [InlineData(ObservationStage.PlaceTargetOnSlit)]
    [InlineData(ObservationStage.SelectAtrExposure)]
    [InlineData(ObservationStage.RunScienceBlock)]
    public void Recorded1500Against2000IsSpecificAndCannotAcquireAutomaticRecovery(ObservationStage stage)
    {
        var gate = Phd2GuidingExposurePolicy.Evaluate(1.5, 2000, "OffSlitGuideStar");
        Assert.Equal(1500, gate.Metrics!["actualGuideExposureMilliseconds"]);
        Assert.False(ObservationAutomaticRecoveryPolicy.For(stage, gate).IsRecoverable);
        var zh = ObservationUiPresentation.Present(stage, gate, CultureInfo.GetCultureInfo("zh-CN"));
        var en = ObservationUiPresentation.Present(stage, gate, CultureInfo.GetCultureInfo("en-US"));
        Assert.DoesNotContain("未分类", zh.Summary);
        Assert.Contains("曝光时间", zh.Summary);
        Assert.Contains("不覆盖", zh.AutomaticRecovery);
        Assert.Contains("无需因此重连", zh.Recommendation);
        Assert.Contains("1500", en.Summary);
        Assert.Contains("2000", en.Summary);
        Assert.False(ObservationUiPresentation.ContainsCjk(en.Summary + en.Recommendation));
        Assert.Equal(ObservationPreviewChannel.G3SlitField,
            ObservationOperatorGuidance.For(stage, gate).PreviewChannel);
    }

    [Fact]
    public void SharedRunnerPreservesFramePublishesGateAndStopsBeforeMeasurementWithoutSettingWrites()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "RealObservationStageRunner.Phd2SlitPlacement.cs"));
        var begin = source.IndexOf("var exposureGate = Phd2GuidingExposurePolicy.Evaluate", StringComparison.Ordinal);
        var end = source.IndexOf("var raw = image.Data.FlatArray", begin, StringComparison.Ordinal);
        var check = source[begin..end];
        Assert.Contains("PublishRunJsonEvidenceAsync", check);
        Assert.Contains("frameSha256 = actualSha", check);
        Assert.Contains("Report(ObservationUiPresentation.Present", check);
        Assert.Contains("throw new PhysicalActionGateException(exposureGate)", check);
        Assert.DoesNotContain("SetExposure", check);
        Assert.DoesNotContain("continue;", check);
        Assert.DoesNotContain("StartGuiding", check);
    }

    [Fact]
    public void CoolingReadyReplacesStaleWaitingProgressBeforeFreshGuideCheck()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "RealObservationStageRunner.cs"));
        var start = source.IndexOf("\"atr-science-temperature-ready\"", StringComparison.Ordinal);
        var end = source.IndexOf("return readiness;", start, StringComparison.Ordinal);
        Assert.Contains("制冷已通过连续稳定检查", source[start..end]);
        Assert.Contains("Report(", source[start..end]);
    }
}
