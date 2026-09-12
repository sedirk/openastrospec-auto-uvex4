using System.Globalization;
using UvexAdv.Nina.Plugin;
using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class ObservationUiPresentationTests
{
    [Fact]
    public void PriorCrossPierReturnIsNotPresentedAsHashOrStarDetectionFailure()
    {
        var issue = ObservationUiPresentation.Present(ObservationStage.ValidateNightSetup,
            GateResult.Unknown("G3_MOTION_CRASH_RETURN_BLOCKED",
                "Durable G3 motion recovery could not return: G3_SEARCH_PIER_SIDE_CHANGED: pierWest to pierEast."),
            new CultureInfo("zh-CN"));
        Assert.Contains("不是哈希不一致或星点识别失败", issue.Summary);
        Assert.Contains("起点残差", issue.Recommendation);
        Assert.Contains("G3_SEARCH_PIER_SIDE_CHANGED", issue.TechnicalDetails);
    }

    [Fact]
    public void SerialConflictNamesThePortAndKeepsTechnicalEvidence()
    {
        const string raw = "UVEX_SERIAL_PORT_RESERVED: COM5 is configured for Dome Drivers/RRCI.Dome; it will not be opened.";
        var chinese = ObservationUiPresentation.PresentUiOperationError(raw, new CultureInfo("zh-CN"));
        Assert.Contains("COM5", chinese.Message);
        Assert.Contains("不要断开屋顶", chinese.Message);
        Assert.Equal(raw, chinese.TechnicalDetails);
        var english = ObservationUiPresentation.PresentUiOperationError(raw, new CultureInfo("en-US"));
        Assert.Equal(raw, english.Message);
        Assert.False(ObservationUiPresentation.ContainsCjk(english.Message));
        var issue = ObservationUiPresentation.Present(ObservationStage.ValidateNightSetup,
            GateResult.Fail("UVEX_AUTO_CONNECT_FAILED", raw), new CultureInfo("zh-CN"));
        Assert.Contains("已配置给其他设备", issue.Summary);
    }

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
    [InlineData("PHD2_SLIT_COMPLETION_WINDOW_EXHAUSTED_RETURNED", "已确认返回原锁点")]
    [InlineData("ATR_SAVED_FRAME_TEMPERATURE_INVALID", "未计入合格帧")]
    [InlineData("PHD2_LOCK_INHERITED_BUDGET_EXHAUSTED", "实际次数、位移、已用时间")]
    [InlineData("PHD2_SCIENCE_GUIDE_EPOCH_CHANGED", "导星会话或狭缝证据已失效")]
    [InlineData("PHD2_SCIENCE_FRESH_SLIT_WINDOW_REJECTED", "同时确认目标身份")]
    [InlineData("PHD2_SCIENCE_IN_PLACE_CHECK_FAILED", "没有因此重建定位")]
    [InlineData("GUIDING_UNSTABLE", "不是单指超出 2 像素")]
    [InlineData("G3_FRAME_REUSED", "拒绝复用旧光谱仪导星相机")]
    [InlineData("G3_SEARCH_NOT_STARTED_RETURNED", "邻场搜索尚未执行")]
    [InlineData("G3_WCS_CENTERING_RETURN_BLOCKED", "回程未取得安全到位确认")]
    [InlineData("G3_MOTION_CRASH_RETURN_BLOCKED", "上轮回程尚未取得到位确认")]
    [InlineData("G3_MOTION_CROSS_PIER_ORIGIN_UNCONFIRMED", "只读坐标核验未确认稳定到位")]
    [InlineData("G3_DESTINATION_PIER_SIDE_CHANGE", "未发送跨侧转向")]
    [InlineData("G3_CATALOG_SHORT_POSITION_UNCONFIRMED", "低增益短曝光")]
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
    public void WcsReturnDriverQueryErrorDoesNotMasqueradeAsStarDetectionOrHashFailure()
    {
        var raw = "G3_DESTINATION_PIER_SIDE_QUERY_FAILED: ASCOM.NotConnectedException (0x80040407): disconnected";
        var presentation = ObservationUiPresentation.Present(
            ObservationStage.AcquireG3SlitField,
            GateResult.Unknown("G3_WCS_CENTERING_RETURN_BLOCKED", raw), Chinese);
        Assert.Contains("赤道仪驱动的目的地侧别查询失败", presentation.Summary);
        Assert.Contains("不是星点识别或哈希错误", presentation.Summary);
        Assert.Contains(raw, presentation.TechnicalDetails);
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
    public void ShortPositionFailureReportsActualFramesShapeAndLocalizedMetrics()
    {
        var metrics = new Dictionary<string, double>
        {
            ["shortPositionFrames"] = 3,
            ["maximumShortPositionFrames"] = 3,
            ["shortOuterContourExtentPixels"] = 14,
            ["shortCoreContourOffsetPixels"] = 3.72,
        };
        var gate = GateResult.Unknown("G3_CATALOG_SHORT_POSITION_UNCONFIRMED",
            "Short position confirmation failed: G3_SHORT_CONTOUR_BLENDED: shape", metrics);
        var chinese = ObservationUiPresentation.Present(ObservationStage.AcquireG3SlitField, gate, Chinese);
        Assert.Contains("内外层轮廓", chinese.Summary);
        Assert.Contains("3/3", chinese.AutomaticRecovery);
        Assert.DoesNotContain("未自动重试", chinese.AutomaticRecovery);
        Assert.Contains("外层轮廓尺寸（像素）=14", ObservationUiPresentation.FormatMetrics(metrics, Chinese));
        var english = ObservationUiPresentation.Present(ObservationStage.AcquireG3SlitField, gate, English);
        Assert.False(ObservationUiPresentation.ContainsCjk(english.AutomaticRecovery));
        Assert.Contains("3/3", english.AutomaticRecovery);
    }

    [Theory]
    [InlineData("G3_SHORT_UNSATURATED_UNMEASURED", "未饱和短帧")]
    [InlineData("G3_SHORT_UNSATURATED_AMBIGUOUS", "多颗独立恒星")]
    public void UnsaturatedShortFrameFailureDoesNotDemandASaturatedCore(string code, string expected)
    {
        var gate = GateResult.Unknown("G3_CATALOG_SHORT_POSITION_UNCONFIRMED", $"Short position confirmation failed: {code}");
        var result = ObservationUiPresentation.Present(ObservationStage.AcquireG3SlitField, gate, Chinese);
        Assert.Contains(expected, result.Summary);
        Assert.DoesNotContain("实心星核", result.Summary);
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
