using System.Globalization;
using UvexAdv.Nina.Plugin;
using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class ObservationUiPresentationTests
{
    [Fact]
    public void ReadOnlyPostLockProgressIsLocalizedWithoutHidingTechnicalIdentity()
    {
        var chinese = ObservationUiPresentation.PresentUiNotice("PHD2_POST_LOCK_OBSERVING", new CultureInfo("zh-CN"));
        var english = ObservationUiPresentation.PresentUiNotice("PHD2_POST_LOCK_OBSERVING", new CultureInfo("en-US"));
        Assert.Contains("不重复启动稳定等待", chinese.Message);
        Assert.Contains("without restarting native settling", english.Message);
        Assert.False(ObservationUiPresentation.ContainsCjk(english.Message));
        Assert.Equal("PHD2_POST_LOCK_OBSERVING", chinese.TechnicalDetails);
    }

    private static readonly CultureInfo Chinese = CultureInfo.GetCultureInfo("zh-CN");
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    [Fact]
    public void EveryStageAndRunStateHasSeparateChineseAndEnglishText()
    {
        foreach (var stage in Enum.GetValues<ObservationStage>())
        {
            var chinese = ObservationUiPresentation.StageName(stage, Chinese);
            var english = ObservationUiPresentation.StageName(stage, English);

            Assert.NotEqual(stage.ToString(), chinese);
            Assert.NotEqual(stage.ToString(), english);
            Assert.True(ObservationUiPresentation.ContainsCjk(chinese));
            Assert.False(ObservationUiPresentation.ContainsCjk(english));
        }

        foreach (var state in Enum.GetValues<ObservationRunState>())
        {
            var chinese = ObservationUiPresentation.RunStateName(state, Chinese);
            var english = ObservationUiPresentation.RunStateName(state, English);

            Assert.NotEqual(state.ToString(), chinese);
            Assert.True(ObservationUiPresentation.ContainsCjk(chinese));
            Assert.False(ObservationUiPresentation.ContainsCjk(english));
        }
    }

    [Theory]
    [InlineData("PHD2_NATIVE_GUIDE_GEOMETRY_REJECTED", "撞到探测器边缘")]
    [InlineData("G3_FRAME_REUSED", "拒绝复用旧光谱仪导星相机")]
    [InlineData("G3_CATALOG_WCS_AUTHORITY_INVALID", "目录/WCS 目标几何证据格式无效")]
    [InlineData("G3_SATURATED_TOPOLOGY_AUTHORITY_INVALID", "饱和目标的目录身份")]
    [InlineData("PHD2_FRESH_SLIT_REACQUISITION_EXHAUSTED", "有界补拍次数已经用尽")]
    [InlineData("PHD2_FRESH_GUIDE_WINDOW_DEADLINE", "剩余时间不足以取得完整")]
    [InlineData("SLIT_LOCUS_LOW_CONFIDENCE", "物理狭缝对比度不足")]
    [InlineData("G3_PLATE_SOLVE_LADDER_EXHAUSTED_DECLARED_INVISIBLE_FIELD", "观测计划明确声明目标")]
    [InlineData("G3_SOLVE_PROBE_OVEREXPOSED", "曝光或天光过亮")]
    [InlineData("G3_CLOUD_OR_TRANSPARENCY_INVALID", "云层或透明度突变")]
    [InlineData("G3_GUIDING_RECOVERY_STOP_CHANGED", "停止证明缺失或已失效")]
    [InlineData("G3_GUIDING_RECOVERY_RETURN_RESERVE_LIMIT", "完整回程超出原剩余预算")]
    [InlineData("G3_GUIDING_RECOVERY_RETURN_UNCONFIRMED", "没有取得到位确认")]
    [InlineData("QHY_MOUNT_COORDINATE_SYNC_READBACK_FAILED", "同步赤道仪坐标后")]
    [InlineData("UVEX_NOT_READY", "UVEX4 服务未返回 Ready")]
    [InlineData("UVEX_SLIT_ILLUMINATION_OFF_UNVERIFIED", "关闭状态未得到完整确认")]
    [InlineData("PHD2_LOCK_RESTART_RETURN_RESIDUAL_MISMATCH", "已经返回恢复原点")]
    public void FrequentCodesHaveSpecificChineseSummaries(string code, string expected)
    {
        var presentation = ObservationUiPresentation.Present(
            ObservationStage.PlaceTargetOnSlit,
            GateResult.Unknown(code, "A complete English adapter sentence should be technical detail only."),
            Chinese);

        Assert.Contains(expected, presentation.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("complete English adapter sentence", presentation.Summary, StringComparison.Ordinal);
        Assert.Contains("complete English adapter sentence", presentation.TechnicalDetails, StringComparison.Ordinal);
        Assert.True(ObservationUiPresentation.ContainsCjk(presentation.Impact));
        Assert.True(ObservationUiPresentation.ContainsCjk(presentation.AutomaticRecovery));
        Assert.True(ObservationUiPresentation.ContainsCjk(presentation.Recommendation));
    }

    [Fact]
    public void WrapperUsesNestedCodeOnlyForPresentationAndPreservesRawOuterCode()
    {
        var gate = GateResult.Unknown(
            "PHD2_SLIT_PLACEMENT_FAILED_SAFE",
            "Placement stopped: G3_FRAME_REUSED: Each staged shift requires a new immutable frame.");

        var presentation = ObservationUiPresentation.Present(
            ObservationStage.PlaceTargetOnSlit,
            gate,
            Chinese);

        Assert.Contains("拒绝复用旧光谱仪导星相机", presentation.Summary, StringComparison.Ordinal);
        Assert.Contains("内部代码 G3_FRAME_REUSED", presentation.Summary, StringComparison.Ordinal);
        Assert.StartsWith("PHD2_SLIT_PLACEMENT_FAILED_SAFE:", presentation.TechnicalDetails, StringComparison.Ordinal);
        Assert.Equal("PHD2_SLIT_PLACEMENT_FAILED_SAFE", gate.Code);
    }

    [Fact]
    public void RecoveryCodeDoesNotMistakeTheLettersCoverInsideRecoveryForAnOpticalCoverFault()
    {
        var presentation = ObservationUiPresentation.Present(
            ObservationStage.PlaceTargetOnSlit,
            GateResult.Unknown(
                "PHD2_LOCK_RECOVERY_FRESH_FIELD_REQUIRED",
                "Fresh reacquisition returned G3_STAR_FIELD_SPARSE_VALID_EXPOSURE."),
            Chinese);

        Assert.Contains("正式光谱仪导星相机/PL3", presentation.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("屋顶", presentation.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("盖板", presentation.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ExhaustedFreshSlitRetryWinsOverItsLastLowConfidenceCause()
    {
        var gate = GateResult.Unknown(
            "PHD2_SLIT_PLACEMENT_FAILED_SAFE",
            "PHD2_FRESH_SLIT_REACQUISITION_EXHAUSTED: 2/3 accepted; " +
            "SLIT_LOCUS_LOW_CONFIDENCE: contrast 2.93; bounded outcome PHD2_FRESH_SLIT_REACQUISITION_EXHAUSTED.");

        var presentation = ObservationUiPresentation.Present(
            ObservationStage.PlaceTargetOnSlit,
            gate,
            Chinese);

        Assert.Contains("有界补拍次数已经用尽", presentation.Summary, StringComparison.Ordinal);
        Assert.Contains("内部代码 PHD2_FRESH_SLIT_REACQUISITION_EXHAUSTED", presentation.Summary, StringComparison.Ordinal);
        Assert.Equal("PHD2_SLIT_PLACEMENT_FAILED_SAFE", gate.Code);
    }

    [Fact]
    public void UnknownMixedLanguageFailureDoesNotPromoteRawEnglishToChineseSummary()
    {
        var presentation = ObservationUiPresentation.Present(
            ObservationStage.AcquireG3SlitField,
            GateResult.Unknown(
                "UNREVIEWED_DETECTOR_FAILURE",
                "中文前缀: This is a complete English sentence from a low-level adapter and must stay technical."),
            Chinese);

        Assert.Contains("当前质量门未通过", presentation.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("complete English sentence", presentation.Summary, StringComparison.Ordinal);
        Assert.Contains("complete English sentence", presentation.TechnicalDetails, StringComparison.Ordinal);
    }

    [Fact]
    public void ManifestWriteFailureExplainsPersistenceInsteadOfGenericQualityFailure()
    {
        var presentation = ObservationUiPresentation.Present(
            ObservationStage.RunScienceBlock,
            GateResult.Unknown("RUN_MANIFEST_WRITE_FAILED", "Observation manifest persistence failed: file in use."),
            Chinese);
        Assert.Contains("运行清单写入失败", presentation.Summary, StringComparison.Ordinal);
        Assert.Contains("本轮不能恢复或记为完成", presentation.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("当前质量门未通过", presentation.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void PassingWarningSaysRunContinuesInsteadOfStopped()
    {
        var presentation = ObservationUiPresentation.Present(
            ObservationStage.StartGuiding,
            GateResult.Warn("PHD2_GUIDING_WIND_SAMPLED_SUPERVISED", "wind sampled"),
            Chinese);

        Assert.Contains("允许本轮继续", presentation.Summary, StringComparison.Ordinal);
        Assert.Contains("继续运行", presentation.Impact, StringComparison.Ordinal);
        Assert.DoesNotContain("已停止", presentation.Impact, StringComparison.Ordinal);
    }

    [Fact]
    public void OrdinaryTimelineStartEventIsNotPresentedAsGateFailure()
    {
        var message = ObservationUiPresentation.EventMessage(
            ObservationStage.AcquireG3SlitField,
            "STAGE_STARTED",
            "Running AcquireG3SlitField.",
            Chinese,
            isAttention: false);

        Assert.Contains("开始执行", message, StringComparison.Ordinal);
        Assert.DoesNotContain("未通过", message, StringComparison.Ordinal);
        Assert.DoesNotContain("已暂停", message, StringComparison.Ordinal);
    }

    [Fact]
    public void RecoverablePolicyDoesNotClaimAnAttemptWithoutExhaustionEvidence()
    {
        var allowed = ObservationUiPresentation.Present(
            ObservationStage.AcquireG3SlitField,
            GateResult.Unknown("G3_CLOUD_OR_TRANSPARENCY_INVALID", "opaque frame"),
            Chinese);
        var exhausted = ObservationUiPresentation.Present(
            ObservationStage.AcquireG3SlitField,
            GateResult.Unknown(
                "G3_CLOUD_OR_TRANSPARENCY_INVALID",
                "opaque frame. Automatic recovery 'RetryWithFreshStageEvidence' exhausted its 3 exact attempts."),
            Chinese);

        Assert.Contains("此代码允许程序", allowed.AutomaticRecovery, StringComparison.Ordinal);
        Assert.Contains("时间线记录实际是否触发", allowed.AutomaticRecovery, StringComparison.Ordinal);
        Assert.Contains("已经用尽", exhausted.AutomaticRecovery, StringComparison.Ordinal);
    }

    [Fact]
    public void EnglishPresentationContainsNoChineseAndKeepsRawDetail()
    {
        const string raw = "PHD2 selected a star outside the detector-edge safety envelope.";
        var presentation = ObservationUiPresentation.Present(
            ObservationStage.StartGuiding,
            GateResult.Fail("PHD2_NATIVE_GUIDE_GEOMETRY_REJECTED", raw),
            English);

        Assert.Equal(raw, presentation.Summary);
        Assert.False(ObservationUiPresentation.ContainsCjk(presentation.Summary));
        Assert.False(ObservationUiPresentation.ContainsCjk(presentation.Impact));
        Assert.False(ObservationUiPresentation.ContainsCjk(presentation.AutomaticRecovery));
        Assert.False(ObservationUiPresentation.ContainsCjk(presentation.Recommendation));
        Assert.Contains(raw, presentation.TechnicalDetails, StringComparison.Ordinal);
    }

    [Fact]
    public void MetricsUseLocalizedCommonNamesButRetainInvariantNumbers()
    {
        var metrics = new Dictionary<string, double>
        {
            ["targetSlitResidualPixels"] = 4.125,
            ["detectedStars"] = 17,
        };

        var chinese = ObservationUiPresentation.FormatMetrics(metrics, Chinese);
        var english = ObservationUiPresentation.FormatMetrics(metrics, English);

        Assert.Contains("目标到狭缝残差=4.125", chinese, StringComparison.Ordinal);
        Assert.Contains("检测星数=17", chinese, StringComparison.Ordinal);
        Assert.Contains("targetSlitResidualPixels=4.125", english, StringComparison.Ordinal);
    }

    [Fact]
    public void LocalUiErrorsKeepCrossLanguageAdapterTextOnlyAsTechnicalDetail()
    {
        var chinese = ObservationUiPresentation.PresentUiOperationError(
            "连接失败：The adapter returned a complete low-level transport error sentence.",
            Chinese);
        var english = ObservationUiPresentation.PresentUiOperationError(
            "连接失败：底层适配器没有返回状态。",
            English);

        Assert.True(ObservationUiPresentation.ContainsCjk(chinese.Message));
        Assert.DoesNotContain("complete low-level", chinese.Message, StringComparison.Ordinal);
        Assert.Contains("complete low-level", chinese.TechnicalDetails, StringComparison.Ordinal);
        Assert.False(ObservationUiPresentation.ContainsCjk(english.Message));
        Assert.True(ObservationUiPresentation.ContainsCjk(english.TechnicalDetails));
    }
}
