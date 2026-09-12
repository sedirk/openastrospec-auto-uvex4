using System.Globalization;
using System.Text.RegularExpressions;
using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

public enum ObservationUiTone
{
    Neutral,
    Info,
    Success,
    Warning,
    Recovering,
    Attention,
    Fault,
}

/// <summary>
/// The operator-facing description of one quality-gate failure.  Machine
/// codes and raw adapter messages remain available as technical evidence, but
/// are deliberately separated from the localized explanation shown first.
/// </summary>
public sealed record ObservationIssuePresentation(
    string Summary,
    string Impact,
    string AutomaticRecovery,
    string Recommendation,
    string TechnicalDetails)
{
    public bool HasTechnicalDetails => !string.IsNullOrWhiteSpace(TechnicalDetails);
}

public sealed record ObservationUiOperationError(string Message, string TechnicalDetails);

/// <summary>
/// Central presentation boundary for stages, run states, quality gates,
/// metrics and adapter failures.  The state machine and evidence retain their
/// invariant English codes; only the desktop presentation is localized.
/// </summary>
public static partial class ObservationUiPresentation
{
    public static string AtrFrameCaption(bool science, int frame, int total, int attempt,
        double exposureSeconds, double highPercentileAdu, double clippedColumns, double lineSnr, CultureInfo culture)
    {
        var label = science
            ? Text($"科学候选 {frame}/{total} · 第 {attempt} 次", $"Science candidate {frame}/{total} · attempt {attempt}", culture)
            : Text($"试拍 {attempt}", $"Probe {attempt}", culture);
        return label + Text(
            $" · {exposureSeconds:G4} s · 强度 p99.9 {highPercentileAdu:F0} ADU · 截断 {clippedColumns:P2} · 谱线 SNR {lineSnr:F1}",
            $" · {exposureSeconds:G4} s · p99.9 {highPercentileAdu:F0} ADU · clipped {clippedColumns:P2} · line SNR {lineSnr:F1}", culture);
    }

    private static readonly IReadOnlyDictionary<string, string> ChineseMetricNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["attempt"] = "尝试次数",
            ["attempts"] = "尝试次数",
            ["searchAttempts"] = "搜索次数",
            ["saturatedCorePixels"] = "星核饱和像素数",
            ["shortWingPixels"] = "可用内翼像素数",
            ["shortWingSnr"] = "内翼信噪比",
            ["shortWingSectors"] = "内翼覆盖扇区数（共8区）",
            ["shortContourSpreadPixels"] = "多层轮廓位置分散（像素）",
            ["shortPositionSpreadPixels"] = "粗定位位置分散尺度（像素）",
            ["shortRepeatSeparationPixels"] = "两张短帧位置差（像素）",
            ["maximumShortRepeatSeparationPixels"] = "短帧一致性上限（像素）",
            ["shortPositionFrames"] = "短曝光复核帧数",
            ["maximumShortPositionFrames"] = "短曝光复核帧数上限",
            ["maximumShortContourSpreadPixels"] = "轮廓一致性上限（像素）",
            ["maximumShortCoreDiameterPixels"] = "饱和星核尺寸上限（像素）",
            ["shortSolidCoreCount"] = "实心星核候选数",
            ["shortCoreWidthPixels"] = "饱和星核宽（像素）",
            ["shortCoreHeightPixels"] = "饱和星核高（像素）",
            ["shortContourAspectRatio"] = "轮廓长短轴比",
            ["shortOuterContourExtentPixels"] = "外层轮廓尺寸（像素）",
            ["shortInnerContourExtentPixels"] = "内层轮廓尺寸（像素）",
            ["maximumShortOuterContourExtentPixels"] = "外层轮廓尺寸上限（像素）",
            ["shortCoreContourOffsetPixels"] = "削顶中心偏移（仅诊断，像素）",
            ["shortSaturatedPixels"] = "短帧识别区饱和像素数",
            ["shortLocalSeedCount"] = "去重后的局部峰数",
            ["shortValidPsfCount"] = "独立有效星像数",
            ["catalogCompanionExpectedSeparationPixels"] = "目录伴星预期间距（像素）",
            ["catalogCompanionVectorResidualPixels"] = "主伴星方向与间距偏差（像素）",
            ["maximumCatalogCompanionVectorResidualPixels"] = "主伴星几何偏差上限（像素）",
            ["shortMaximumLocalPeakSigma"] = "最大局部峰显著性（σ）",
            ["shortMaximumPeakAdu"] = "局部峰值（ADU）",
            ["minimumShortUnsaturatedContourPixels"] = "未饱和轮廓最少像素数",
            ["shortShapeRejectedCount"] = "轮廓未通过数",
            ["shortSecondaryPeakRejectedCount"] = "多峰混合未通过数",
            ["shortContourPixelRejectedCount"] = "轮廓像素支撑不足数",
            ["shortContourBoundaryRejectedCount"] = "轮廓触及测量边界数",
            ["shortContourAspectRejectedCount"] = "轮廓长短轴比异常数",
            ["shortContourHollowRejectedCount"] = "轮廓中心空心数",
            ["shortContourExtentRejectedCount"] = "内外轮廓尺寸不符数",
            ["shortMiddleContourPixels"] = "中层轮廓像素数",
            ["shortInnerContourPixels"] = "内层轮廓像素数",
            ["shortInnerSamplingAllowancePixels"] = "内层采样边界余量（像素）",
            ["shortSupportRejectedCount"] = "像素或信号不足数",
            ["shortLocalBackgroundAdu"] = "星像周围背景（ADU）",
            ["shortLocalBackgroundSigmaAdu"] = "星像周围背景波动（ADU）",
            ["shortLocalPeakSigma"] = "星像局部峰显著性（σ）",
            ["shortPsfSnr"] = "未饱和星像信噪比",
            ["shortPsfPixels"] = "未饱和星像轮廓像素数",
            ["shortPsfSectors"] = "未饱和星像覆盖扇区数（共8区）",
            ["searchCumulativeMotionArcseconds"] = "搜索累计运动",
            ["residualArcseconds"] = "残差",
            ["targetSlitResidualPixels"] = "目标到狭缝残差",
            ["guideLockResidualPixels"] = "导星锁点残差",
            ["detectedStars"] = "检测星数",
            ["saturatedFraction"] = "饱和比例",
            ["transparency"] = "透明度",
            ["fwhmPixels"] = "FWHM",
            ["uniquenessRatio"] = "唯一性比值",
            ["edgeDistancePixels"] = "边缘距离",
            ["elapsedSeconds"] = "已用时间",
            ["guideEpoch"] = "导星纪元",
            ["connectionEpoch"] = "连接纪元",
            ["maximumAttempts"] = "次数上限",
            ["maximumCumulativePixels"] = "累计像素上限",
            ["cumulativeCommandedPixels"] = "累计命令像素",
            ["settleDistancePixels"] = "稳定圆残差",
            ["settleTimeSeconds"] = "稳定持续时间",
            ["mountResidualArcseconds"] = "赤道仪读回残差",
            ["simulationConfidence"] = "模拟置信度",
        };

    public static bool IsChinese(CultureInfo? culture = null) =>
        string.Equals(
            (culture ?? CultureInfo.CurrentUICulture).TwoLetterISOLanguageName,
            "zh",
            StringComparison.OrdinalIgnoreCase);

    public static string Text(string chinese, string english, CultureInfo? culture = null) =>
        IsChinese(culture) ? chinese : english;

    public static string StageName(ObservationStage stage, CultureInfo? culture = null) =>
        CameraRoleLabels.Description(IsChinese(culture)
            ? stage switch
            {
                ObservationStage.ValidateNightSetup => "验证并锁定本夜配置（Night Setup）",
                ObservationStage.SlewToCatalogTarget => "按目录坐标转向目标",
                ObservationStage.AcquireQhyWideField => "QHY/GS350 广域解算见证",
                ObservationStage.CoarseCenter => "校验 QHY 见证并交接 G3",
                ObservationStage.AcquireG3SlitField => "G3 解算、大步修正或邻场搜索",
                ObservationStage.PlaceTargetOnSlit => "PHD2 精确送入狭缝中点",
                ObservationStage.StartGuiding => "PHD2 选星、导星与稳定判定",
                ObservationStage.StartQhyPhotometry => "启动 QHY 同步测光",
                ObservationStage.SelectAtrExposure => "ATR 探测曝光与档位选择",
                ObservationStage.RunScienceBlock => "ATR 科学曝光与健康监测",
                ObservationStage.FinalizeObservation => "结束测光并执行安全收尾",
                _ => stage.ToString(),
            }
            : stage switch
            {
                ObservationStage.ValidateNightSetup => "Validate and lock Night Setup",
                ObservationStage.SlewToCatalogTarget => "Slew to catalogue target",
                ObservationStage.AcquireQhyWideField => "Acquire QHY/GS350 wide-field witness",
                ObservationStage.CoarseCenter => "Validate QHY witness and hand off to G3",
                ObservationStage.AcquireG3SlitField => "Solve G3 field, correct or search neighbours",
                ObservationStage.PlaceTargetOnSlit => "Place target at slit centre with PHD2",
                ObservationStage.StartGuiding => "Select guide star, guide and settle in PHD2",
                ObservationStage.StartQhyPhotometry => "Start simultaneous QHY photometry",
                ObservationStage.SelectAtrExposure => "Probe ATR exposure and select tier",
                ObservationStage.RunScienceBlock => "Run ATR science block and health checks",
                ObservationStage.FinalizeObservation => "Stop photometry and perform safe cleanup",
                _ => stage.ToString(),
            }, culture);

    public static string RunStateName(ObservationRunState state, CultureInfo? culture = null) =>
        IsChinese(culture)
            ? state switch
            {
                ObservationRunState.Idle => "空闲",
                ObservationRunState.Validating => "正在验证",
                ObservationRunState.RunningAuto => "自动推进",
                ObservationRunState.PauseRequested => "正在完成当前有界动作，随后暂停",
                ObservationRunState.Paused => "已暂停",
                ObservationRunState.PausedNeedsAttention => "已安全暂停，等待处理",
                ObservationRunState.ManualTakeover => "人工接管",
                ObservationRunState.Cancelling => "正在取消",
                ObservationRunState.Finalizing => "正在安全收尾并写入清单",
                ObservationRunState.Completed => "已完成",
                ObservationRunState.Cancelled => "已取消",
                ObservationRunState.Faulted => "故障，自动流程已停止",
                _ => state.ToString(),
            }
            : state switch
            {
                ObservationRunState.Idle => "Idle",
                ObservationRunState.Validating => "Validating",
                ObservationRunState.RunningAuto => "Running automatically",
                ObservationRunState.PauseRequested => "Finishing bounded action, then pausing",
                ObservationRunState.Paused => "Paused",
                ObservationRunState.PausedNeedsAttention => "Safely paused; attention required",
                ObservationRunState.ManualTakeover => "Manual takeover",
                ObservationRunState.Cancelling => "Cancelling",
                ObservationRunState.Finalizing => "Performing safe cleanup and writing manifest",
                ObservationRunState.Completed => "Completed",
                ObservationRunState.Cancelled => "Cancelled",
                ObservationRunState.Faulted => "Faulted; automation stopped",
                _ => state.ToString(),
            };

    public static string GateStateName(GateResult gate, CultureInfo? culture = null) =>
        IsChinese(culture)
            ? gate switch
            {
                { Disposition: GateDisposition.Passed, Severity: GateSeverity.Warning } => "警告后继续",
                { Disposition: GateDisposition.Passed } => "通过",
                { Disposition: GateDisposition.Failed } => "未通过，已暂停",
                { Disposition: GateDisposition.Indeterminate } => "证据不足，已暂停",
                _ => gate.Disposition.ToString(),
            }
            : gate switch
            {
                { Disposition: GateDisposition.Passed, Severity: GateSeverity.Warning } => "Warning; continued",
                { Disposition: GateDisposition.Passed } => "Passed",
                { Disposition: GateDisposition.Failed } => "Failed; paused",
                { Disposition: GateDisposition.Indeterminate } => "Insufficient evidence; paused",
                _ => gate.Disposition.ToString(),
            };

    public static string Waiting(CultureInfo? culture = null) => Text("等待", "Waiting", culture);
    public static string NotRun(CultureInfo? culture = null) => Text("尚未执行", "Not run yet", culture);
    public static string RunLabel(CultureInfo? culture = null) => Text("运行", "Run", culture);

    public static string FormatMetrics(
        IReadOnlyDictionary<string, double>? metrics,
        CultureInfo? culture = null)
    {
        if (metrics is null || metrics.Count == 0) return "—";
        var chinese = IsChinese(culture);
        return string.Join(
            " · ",
            metrics.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair =>
                {
                    var name = chinese && ChineseMetricNames.TryGetValue(pair.Key, out var translated)
                        ? translated
                        : pair.Key;
                    return $"{name}={pair.Value.ToString("0.####", CultureInfo.InvariantCulture)}";
                }));
    }

    public static ObservationIssuePresentation Present(
        ObservationStage stage,
        GateResult gate,
        CultureInfo? culture = null)
    {
        ArgumentNullException.ThrowIfNull(gate);
        var chinese = IsChinese(culture);
        if (gate.Disposition == GateDisposition.Passed)
        {
            var warning = gate.Severity == GateSeverity.Warning;
            var passedSummary = DisplayPassedMessage(gate, chinese);
            return new ObservationIssuePresentation(
                CameraRoleLabels.Description(passedSummary, culture),
                warning
                    ? Text("本轮继续运行，但后续证据会保留降级/警告标记。", "The run continues, with the downgrade/warning retained in subsequent evidence.", culture)
                    : Text("本阶段已通过，可以按计划继续。", "This stage passed and may continue as planned.", culture),
                Text("无需执行阻断恢复。", "No blocking recovery is required.", culture),
                warning
                    ? Text("在质量门和时间线中保留该警告；若指标继续恶化，后续硬门仍会暂停。", "Keep the warning in the quality-gate history; a later hard gate will pause if the metric degrades further.", culture)
                    : Text("无需操作。", "No operator action is required.", culture),
                string.IsNullOrWhiteSpace(gate.Message) ? gate.Code : $"{gate.Code}: {gate.Message.Trim()}");
        }
        var effectiveCode = EffectiveCode(gate.Code, gate.Message);
        var summary = chinese
            ? DescribeSerialBindingIssue(gate.Code + ": " + gate.Message) ?? ChineseIssueSummary(gate.Code, effectiveCode, gate.Message)
            : EnglishIssueSummary(gate.Code, effectiveCode, gate.Message);
        var impact = Impact(stage, chinese);
        var recovery = Recovery(stage, gate, chinese);
        var recommendation = Recommendation(stage, gate.Code, effectiveCode, chinese);
        var technical = string.IsNullOrWhiteSpace(gate.Message)
            ? gate.Code
            : $"{gate.Code}: {gate.Message.Trim()}";
        return new ObservationIssuePresentation(
            CameraRoleLabels.Description(summary, culture),
            CameraRoleLabels.Description(impact, culture),
            CameraRoleLabels.Description(recovery, culture),
            CameraRoleLabels.Description(recommendation, culture), technical);
    }

    public static string EventMessage(
        ObservationStage? stage,
        string? code,
        string? rawMessage,
        CultureInfo? culture = null,
        bool isAttention = false)
    {
        var message = rawMessage?.Trim() ?? string.Empty;
        if (!IsChinese(culture)) return CameraRoleLabels.Description(message, culture);
        if (string.IsNullOrWhiteSpace(code))
        {
            if (ContainsCjk(message) && !ContainsEnglishSentence(message)) return CameraRoleLabels.Description(message, culture);
            return "运行组件返回了未分类的技术状态；原始消息已保留在时间线技术详情中。";
        }

        var resolvedStage = stage ?? ObservationStage.ValidateNightSetup;
        if (isAttention)
        {
            return Present(resolvedStage, GateResult.Unknown(code, message), culture).Summary;
        }

        if (ContainsCjk(message) && !ContainsEnglishSentence(message)) return CameraRoleLabels.Description(message, culture);
        return code switch
        {
            "PLAN_VALIDATING" => "正在验证观测计划与锁定配置。",
            "RUN_STARTED" => "自动观测已经启动。",
            "RUN_RESUMED" => "已重新核验过期门，自动流程继续。",
            "PAUSE_REQUESTED" => "已请求暂停；当前有界动作结束后不会开始下一动作。",
            "RUN_PAUSED" => "自动流程已在动作边界暂停。",
            "RUN_CANCELLED" => "自动观测已取消，原始证据仍保留。",
            "RUN_COMPLETED" => "自动观测与必要收尾已完成。",
            "STAGE_STARTED" => $"开始执行：{StageName(resolvedStage, culture)}。",
            "STAGE_PASSED" => $"已完成：{StageName(resolvedStage, culture)}。",
            _ when code.EndsWith("_STARTED", StringComparison.Ordinal) => $"已开始：{StageName(resolvedStage, culture)}。",
            _ when code.EndsWith("_PASSED", StringComparison.Ordinal) ||
                   code.EndsWith("_COMPLETED", StringComparison.Ordinal) ||
                   code.EndsWith("_READY", StringComparison.Ordinal) ||
                   code.EndsWith("_OK", StringComparison.Ordinal) ||
                   code.EndsWith("_VALID", StringComparison.Ordinal) ||
                   code.EndsWith("_LOCKED", StringComparison.Ordinal) ||
                   code.EndsWith("_STABLE", StringComparison.Ordinal) ||
                   code.EndsWith("_OPEN", StringComparison.Ordinal) ||
                   code.EndsWith("_HEALTHY", StringComparison.Ordinal) ||
                   code.EndsWith("_VERIFIED", StringComparison.Ordinal) ||
                   code.EndsWith("_ACCEPTED", StringComparison.Ordinal) => $"{StageName(resolvedStage, culture)}已通过。",
            _ when code.Contains("WARNING", StringComparison.Ordinal) ||
                   code.Contains("DEGRADED", StringComparison.Ordinal) => $"{StageName(resolvedStage, culture)}产生一条继续运行警告。",
            _ => $"运行事件 {code}：{StageName(resolvedStage, culture)}。",
        };
    }

    public static string EvidenceKind(string kind, CultureInfo? culture = null)
    {
        if (!IsChinese(culture)) return kind;
        return CameraRoleLabels.Description(kind switch
        {
            "g3-slit-illumination-sequence" => "G3 狭缝照明序列",
            "g3-bounded-search-summary" => "G3 有界搜索摘要",
            "g3-plate-solve-frame" => "G3 解算帧",
            "g3-phd2-guide-selection-frame" => "PHD2/G3 导星选星帧",
            "phd2-lock-shift-fresh-residual" => "PHD2 入缝新鲜残差",
            "phd2-post-lock-readonly-window" => "PHD2 微调后只读导星窗口（有人监督）",
            "phd2-full-frame-guide-takeover" => "PHD2 全幅导星接管",
            "qhy-mount-coordinate-sync-intent" => "QHY WCS 坐标同步意图",
            "qhy-acquisition-frame" => "QHY 广域采集帧",
            "atr-probe" => "ATR 探测曝光",
            _ => $"其他运行证据（{kind}）",
        }, culture);
    }

    public static bool ContainsCjk(string? value) =>
        !string.IsNullOrEmpty(value) && value.Any(character => character is >= '\u3400' and <= '\u9FFF');

    public static ObservationUiOperationError PresentUiOperationError(
        string? rawMessage,
        CultureInfo? culture = null)
    {
        var raw = rawMessage?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(raw))
        {
            return new ObservationUiOperationError(raw, string.Empty);
        }

        if (!IsChinese(culture))
        {
            if (!ContainsCjk(raw)) return new ObservationUiOperationError(raw, string.Empty);
            return new ObservationUiOperationError(
                ObservationStaticTextCatalog.Translate(raw, culture),
                raw);
        }

        if (DescribeSerialBindingIssue(raw) is { } serialIssue)
            return new ObservationUiOperationError(serialIssue, raw);
        if (!ContainsEnglishSentence(raw)) return new ObservationUiOperationError(NormalizeChineseTerminology(raw), string.Empty);

        var match = EnglishSentenceRegex().Match(raw);
        var prefix = match.Success ? raw[..match.Index].Trim().TrimEnd(':', '：', '-', '—') : string.Empty;
        var summary = ContainsCjk(prefix)
            ? $"{prefix}：底层组件返回了错误；原始详情见“技术详情”。"
            : "本次界面操作未完成；底层组件的原始错误见“技术详情”。";
        return new ObservationUiOperationError(summary, raw);
    }

    public static ObservationUiOperationError PresentUiNotice(
        string? rawMessage,
        CultureInfo? culture = null)
    {
        var raw = rawMessage?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(raw)) return new ObservationUiOperationError(string.Empty, string.Empty);

        if (raw == "PHD2_POST_LOCK_OBSERVING")
            return new ObservationUiOperationError(Text(
                "PHD2 微调后继续导星，正在检查锁点与连续新帧；不重复启动稳定等待。",
                "PHD2 continues guiding after the fine lock shift; checking lock readback and fresh frames without restarting native settling.",
                culture), raw);

        if (IsChinese(culture))
        {
            var localized = CameraRoleLabels.Description(NormalizeChineseTerminology(raw), culture);
            return ContainsEnglishSentence(raw)
                ? new ObservationUiOperationError("界面操作状态已更新；原始技术详情可展开查看。", raw)
                : new ObservationUiOperationError(localized, localized == raw ? string.Empty : raw);
        }

        if (!ContainsCjk(raw)) return new ObservationUiOperationError(raw, string.Empty);
        var translated = ObservationStaticTextCatalog.Translate(raw, culture);
        return new ObservationUiOperationError(translated, raw);
    }

    private static string? DescribeSerialBindingIssue(string raw)
    {
        var code = Regex.Match(raw, @"\bUVEX_SERIAL_[A-Z_]+\b").Value;
        var port = Regex.Match(raw, @"\bCOM[1-9][0-9]*\b", RegexOptions.IgnoreCase).Value;
        var endpoint = string.IsNullOrEmpty(port) ? "已保存串口" : port;
        return code switch
        {
            "UVEX_SERIAL_PORT_RESERVED" => $"UVEX 的 {endpoint} 已配置给其他设备，未打开该端口；请重新辨认并绑定 UVEX，不要断开屋顶等设备来抢占串口。",
            "UVEX_SERIAL_PORT_NOT_PRESENT" => $"未找到 {endpoint} 对应的唯一在线 USB 设备；USB 换口后需重新辨认并保存 UVEX 绑定。",
            "UVEX_SERIAL_BINDING_REQUIRED" => $"{endpoint} 尚未保存经辨认的 UVEX USB 实例；仅有 CH340 型号不能确认设备身份。",
            "UVEX_SERIAL_BINDING_CHANGED" => $"{endpoint} 的 USB 实例与保存绑定不符；已停止连接，请在空闲状态重新辨认 UVEX。",
            "UVEX_SERIAL_IDENTITY_MISMATCH" => $"{endpoint} 未返回有效的 UVEX4 固件版本与设备描述，未授权机构控制。",
            "UVEX_SERIAL_IDENTITY_UNVERIFIED" => $"{endpoint} 尚无本次连接的 USB 与 UVEX4 协议身份确认；请更新并重新连接 UVEX 服务。",
            "UVEX_SERIAL_PORT_INVALID" => "UVEX 串口设置无效；必须指定单个明确的 COM 端口，不能用自动扫描或路径代替。",
            _ => null,
        };
    }

    private static string ChineseIssueSummary(string outerCode, string effectiveCode, string raw)
    {
        var description = effectiveCode switch
        {
            "STAGE_EXCEPTION" => "当前阶段发生未分类异常，流程已在动作边界停止",
            "PHD2_GUIDING_FRAME_TIMEOUT" => "PHD2 未在有界等待内交付新的导星帧，不能确认当前目标位置；这不是目标太暗或导星精度不合格的判定",
            "PHD2_OWNER_STATUS_TIMEOUT" => "PHD2 状态查询超时，暂时无法确认导星相机所有者的实际状态；新的曝光已停止",
            "PHD2_SLIT_PLACEMENT_FAILED_SAFE" => "PHD2 入缝没有取得可验证结果，流程已安全停止",
            "PHD2_GUIDE_OUTPUT_UNAVAILABLE" => "PHD2 连续请求修正但没有导星脉冲输出；连接标志和星点位置不代表赤道仪连接有效",
            "PHD2_GUIDE_OUTPUT_RETURN_PENDING" => "PHD2 导星脉冲输出失效，已停止；仍有未结精调责任，未伪报物理回程或重置预算",
            "PHD2_GUIDE_OUTPUT_STOP_UNCONFIRMED" => "PHD2 导星输出失效且本轮停止未获确认；未重连、未发送新的移动或曝光",
            "PHD2_GUIDE_OUTPUT_RECOVERY_EXHAUSTED" => "已原生重连一次，导星脉冲输出仍失效；已停止，不再循环消耗粗定位预算",
            "PHD2_GUIDE_OUTPUT_RECONNECT_FAILED" => "PHD2 导星输出故障后的原生重连或身份复核失败；未继续曝光或重置预算",
            "PHD2_SLIT_COMPLETION_WINDOW_EXHAUSTED" => "入缝新帧未在本轮时限内达到所选质量策略；已保留回程时间，没有重新解算或追加运动",
            "PHD2_SLIT_COMPLETION_WINDOW_EXHAUSTED_RETURNED" => "入缝新帧未在本轮时限内达到所选质量策略；已确认返回原锁点并停止导星，没有再做无预算的全场重建",
            "PHD2_LOCK_INHERITED_BUDGET_EXHAUSTED" => "本轮入缝账本的剩余预算不足；请查看实际次数、位移、已用时间和原始原因，不代表继承了前夜用量",
            "PHD2_SCIENCE_RECOVERY_BUDGET_UNAVAILABLE" => "导星证据需要重建，但原精调预算不能授权新的移动；未启动回退定位，已保存光谱保留",
            "PHD2_SCIENCE_GUIDE_EPOCH_CHANGED" => "试拍前的导星会话或狭缝证据已失效；需要有界复核，不能用旧证据开始下一帧",
            "PHD2_SCIENCE_FRESH_SLIT_WINDOW_REJECTED" => "试拍前的新帧未能同时确认目标身份、导星连续性与所选入缝质量；没有追加移动或曝光",
            "PHD2_SCIENCE_IN_PLACE_CHECK_FAILED" => "原位复核导星与狭缝证据时发生异常；已保留原始原因，没有因此重建定位或追加移动",
            "PHD2_LOCK_RECOVERY_FRESH_FIELD_REQUIRED" => "PHD2 旧锁点账本恢复时未取得新的正式 G3/PL3 目标与狭缝联合证据",
            "PHD2_LOCK_RECOVERY_FOREIGN_ENDPOINT_UNPROVEN" => "旧观测留下的 PHD2 锁点端点无法由持久化读回唯一确认；没有发送返回命令",
            "PHD2_LOCK_RECOVERY_SLIT_STATE_CHANGED" => "同一目标的锁点恢复中，fresh 物理狭缝位置与原账本不一致",
            "SLIT_LOCK_RETURN_TIME_RESERVE" => "PHD2 锁点返程所需的最坏耗时无法装入当前活动恢复时窗；没有发送锁点命令",
            "SLIT_LOCK_RETURN_CUMULATIVE_RESERVE" => "当前精调下一步与返回原锁点所需的总位移超过剩余预算；未追加精调，原账本与回程责任保留",
            "SLIT_RESIDUAL_SEARCH_WINDOW" => "实测目标离狭缝太远，超出精调接管范围；需要先重新完成 WCS 粗居中，不是导星精度警告",
            "G3_COARSE_HANDOFF_REQUIRED" => "目标虽已入画，但尚未进入狭缝附近的精调交接范围；未跳过 WCS 粗居中",
            "PHD2_WCS_TAKEOVER_POSITION_CHANGED" => "导星接管新帧的目标位置与 WCS 交接位置差异过大；先重建定位，未盲目追加精调",
            "SLIT_LOCK_ACQUISITION_CUMULATIVE_RESERVE" or "SLIT_LOCK_ACQUISITION_ATTEMPT_RESERVE" or "SLIT_LOCK_ACQUISITION_TIME_RESERVE" => "完整精调及安全回程无法装入当前剩余预算；在第一步锁点微调前发现，先尝试有界 WCS 重新居中",
            "PHD2_COARSE_HANDOFF_RECOVERY_EXHAUSTED" => "已自动重建一次 WCS／导星交接，位置或完整精调预算仍未满足；停止追加动作并保留原始测量与账本",
            "PHD2_COARSE_HANDOFF_RETURN_REQUIRED" => "交接重建前仍有未结精调动作，须先核验原锁点回程；未重置预算或启动新的粗定位",
            "PHD2_LOCK_RESTART_RETURN_RESIDUAL_MISMATCH" => "PHD2 锁点已经返回恢复原点，但 fresh G3 星场尚未证明本次光学响应；流程保持停止且不会重复发送锁点命令",
            "PHD2_LOCK_RETURN_G3_REACQUISITION_BLOCKED" => "旧 PHD2 锁点债务已经安全结清，但自动重建 fresh G3/PL3/狭缝证据未通过；没有复用不稳定旧场",
            "PHD2_NATIVE_GUIDE_GEOMETRY_REJECTED" => "PHD2 选中的导星星撞到探测器边缘、目标光晕或狭缝保护区",
            "PHD2_OFF_SLIT_NATIVE_SELECTION_EXHAUSTED" => "PHD2 已用完旁星有界重选次数，仍未找到满足几何限制的导星星",
            "G3_FRAME_REUSED" => "精调阶段拒绝复用旧 G3 残差帧；每次位移后都必须取得新帧",
            "G3_CATALOG_WCS_AUTHORITY_INVALID" => "目录/WCS 目标几何证据格式无效，不能据此授权入缝位移",
            "G3_SATURATED_TOPOLOGY_AUTHORITY_INVALID" => "饱和目标的目录身份、实心核质心或光通量不适用标记不一致，不能据此授权入缝位移",
            "PHD2_FRESH_SLIT_REACQUISITION_EXHAUSTED" => "连续 fresh 导星帧仍无法可靠辨认物理狭缝，有界补拍次数已经用尽",
            "PHD2_FRESH_GUIDE_WINDOW_DEADLINE" => "微调阶段的剩余时间不足以取得完整的新目标/狭缝测量窗口；不是已测整组导星精度不合格",
            "SLIT_LOCUS_LOW_CONFIDENCE" => "fresh 导星帧中的物理狭缝对比度不足，当前帧不能授权入缝位移",
            "G3_RUN_LED_SLIT_GEOMETRY_INVALID" => "本轮 LED 找缝证据与当前相机、狭缝或设备状态不再匹配，需在安全采集边界重新找缝；不是目标太暗",
            "RUN_MANIFEST_WRITE_FAILED" => "运行清单写入失败，后续结果无法可靠入账；本轮不能恢复或记为完成，已保存的原始帧保留。请排查文件占用或磁盘问题，在安全结束本轮后重新开始",
            "PHD2_PLACEMENT_SETTLE_STALE" => "入缝后保存的 PHD2 导星或稳定纪元已变化，旧证据不能继续使用",
            "PHD2_CALIBRATION_PRE_GUIDE_REJECTED" => "当前 PHD2 校准未通过导星前质量门，未发送导星命令",
            "PHD2_RECALIBRATION_DID_NOT_BECOME_ACTIVE" => "已执行一次强制校准，但 PHD2 仍未报告可用的当前校准",
            "PHD2_POST_CALIBRATION_SETTLE_FAILED" => "PHD2 重新校准后未在规定时间内取得稳定证据",
            "PHD2_GUIDING_SUPERVISED_ONLY" => "当前 PHD2 校准只允许有人监督运行，未授予无人值守科学曝光权限",
            "GUIDING_LOST" => "当前导星会话或有效证据已丢失，新的科学曝光暂未获准；不单凭此代码判断云或设备故障",
            "GUIDING_UNSTABLE" or "GUIDING_NOT_STABLE" => "当前导星连续性或稳定证据不足；已保存的光谱保留，复核通过后才能继续，不是单指超出 2 像素",
            "G3_BOUNDED_SEARCH_EXHAUSTED_RETURNED" => "G3 邻场搜索未找到可接受目标，赤道仪已返回保存的搜索起点",
            "G3_SEARCH_NOT_STARTED_RETURNED" => "邻场搜索尚未执行：首步前置检查未通过，赤道仪已确认返回保存的起点；请查看预算数值与原始原因，不是已搜索但未找到目标",
            "G3_CATALOG_SHORT_POSITION_UNCONFIRMED" => ShortPositionIssue(raw),
            "G3_MOTION_CRASH_RETURN_BLOCKED" => raw.Contains("G3_SEARCH_PIER_SIDE_CHANGED", StringComparison.Ordinal)
                ? "上轮回程记录与赤道仪当前侧别不同；不能执行跨侧旧位移，尚未确认旧回程到位。不是哈希不一致或星点识别失败"
                : "上轮回程尚未取得到位确认；请查看原始回程原因，不能通过重启直接视为完成",
            "G3_MOTION_CROSS_PIER_ORIGIN_UNCONFIRMED" => "上轮最后回程已跨侧，但只读坐标核验未确认稳定到位；请查看起点残差、读回漂移和原始原因。未执行旧位移，旧记录保留",
            "G3_WCS_CENTERING_RETURN_BLOCKED" => raw.Contains("G3_SEARCH_PIER_SIDE_CHANGED", StringComparison.Ordinal)
                ? "WCS 居中回程期间赤道仪报告侧别改变；未确认返回起点，旧回程账本保留，不能跨侧继续执行旧位移"
                : raw.Contains("G3_DESTINATION_PIER_SIDE_CHANGE", StringComparison.Ordinal)
                    ? "驱动预测 WCS 回程会改变赤道仪侧别，已在发令前停止；未确认返回起点，旧回程账本保留"
                    : raw.Contains("G3_DESTINATION_PIER_SIDE_QUERY_FAILED", StringComparison.Ordinal)
                        ? "赤道仪驱动的目的地侧别查询失败，WCS 回程未完成；请查看驱动原始异常。不是星点识别或哈希错误，旧运动账本保留"
                        : "WCS 居中后的回程未取得安全到位确认；不是单纯未找到星点，请查看原始回程原因，旧运动账本保留",
            "G3_DESTINATION_PIER_SIDE_CHANGE" => "驱动预测目的地会改变赤道仪侧别；未发送跨侧转向，旧运动账本保留",
            "G3_DESTINATION_PIER_SIDE_QUERY_FAILED" => "赤道仪目的地侧别查询失败；尚未发送本次转向，请查看驱动原始错误",
            "G3_WCS_CENTERING_BUDGET_EXHAUSTED_RETURNED" => "导星相机 WCS 粗居中的运动、次数或回程时间预算不足；赤道仪已返回起点，没有继续邻场搜索",
            "G3_MOTION_RETURN_INTERMEDIATE_RESIDUAL_LIMIT" => "G3 回原中间段未进入严格 2″ 命令包络；已保存 fresh 读回并停止后续回程段",
            "G3_GUIDING_RECOVERY_STOP_CHANGED" or "G3_GUIDING_RECOVERY_STOP_REQUIRED" => "导星中断后的停止证明缺失或已失效；为避免与 PHD2 同时驱动赤道仪，没有发送回程",
            "G3_GUIDING_RECOVERY_CONTEXT_INVALID" or "G3_GUIDING_RECOVERY_LEDGER_CHANGED" or "G3_GUIDING_RECOVERY_OWNER_CHANGED" => "导星后回程的设备身份或原运动账本已改变；没有覆盖账本或接续运动",
            "G3_GUIDING_RECOVERY_GEOMETRY_INVALID" or "G3_GUIDING_RECOVERY_POSITION_LIMIT" => "导星后回程坐标无效或超出原有运动范围；未扩大位置门限",
            "G3_GUIDING_RECOVERY_RETURN_RESERVE_LIMIT" => "导星后回原点所需的完整回程超出原剩余预算；未发送新的回程动作，也未重置预算",
            "G3_GUIDING_RECOVERY_RETURN_UNCONFIRMED" => "导星中断后的回原点没有取得到位确认；后续定位与曝光保持停止，实际回程详情已保留",
            "G3_FIELD_MOUNT_BINDING_STALE" => "G3 图像与赤道仪读回的时间绑定已过期，旧帧不能授权运动",
            "G3_FIELD_MOUNT_BINDING_FRAME_MISSING" => "G3 运动证据所引用的原始帧不存在",
            "G3_FIELD_MOUNT_BINDING_FRAME_UNREADABLE" => "G3 运动证据所引用的原始帧无法读取",
            "G3_FIELD_MOUNT_BINDING_READBACK_UNAVAILABLE" => "拍摄 G3 帧时没有取得可信的赤道仪位置读回",
            "G3_PLATE_SOLVE_LADDER_TRANSIENT_EXHAUSTED" => "G3 解算曝光阶梯遇到连续临时故障，本轮新鲜阶梯已经用尽",
            "G3_PLATE_SOLVE_LADDER_EXHAUSTED_STRUCTURED_FIELD" => "G3 三档长曝光均取得结构化星场，但 PL3 未能建立 WCS；不会把有星点误判成已解算",
            "G3_PLATE_SOLVE_LADDER_EXHAUSTED_DECLARED_INVISIBLE_FIELD" => "G3 解算阶梯未取得 WCS 或可见星点；仅因观测计划明确声明目标在 G3 中可能不可见，才允许继续目录/WCS 有界恢复",
            "G3_SOLVE_PROBE_OVEREXPOSED" => "G3 解算帧有大面积像素达到饱和值，当前曝光或天光过亮；已停止更长曝光和邻场移动",
            "G3_CLOUD_OR_TRANSPARENCY_INVALID" => "G3 新帧没有足够的一致星点，当前更像云层或透明度突变而不是位置错误",
            "G3_STAR_FIELD_SPARSE_VALID_EXPOSURE" => "G3 短曝光星场形态学只能证明曝光有效，仍需正式 PL3/WCS 建立目标位置",
            "BRIGHT_TARGET_ONLY_ANNULAR_GHOSTS" => "亮目标分析只找到空心环状鬼影，没有找到可作为真目标的实心核心",
            "BRIGHT_TARGET_TOPOLOGY_UNPROVEN" => "亮目标与鬼影的拓扑关系仍不唯一，目标身份尚未证明",
            "SLIT_LED_IDENTITY_GEOMETRY_UNAVAILABLE" => "狭缝灯差分图未能恢复可信的物理狭缝几何",
            "QHY_MOUNT_COORDINATE_SYNC_READBACK_FAILED" => "QHY WCS 同步赤道仪坐标后，读回残差仍超过允许值；没有发送 Slew",
            "QHY_SOLVE_REQUIRED" => "尚无可接受的 QHY/PL3 广域解算，不能进入后续粗定位",
            "QHY_COARSE_SOURCE_FRAME_MISSING" or "QHY_NATIVE_CENTER_SOURCE_FRAME_MISSING" => "粗定位所需的 QHY 原始帧缺失",
            "QHY_NATIVE_CENTER_FRESH_SOURCE_REQUIRED" => "粗定位只拿到旧 QHY 帧，必须重新采集新鲜见证帧",
            "UVEX_NOT_READY" => "UVEX4 服务未返回 Ready 和可信位置，机构动作被禁止",
            "UVEX_AUTO_CONNECT_FAILED" => "未能通过服务保存的串口绑定连接并验证 UVEX4",
            "UVEX_SLIT_ILLUMINATION_OFF_UNVERIFIED" => "狭缝定位 LED 的关闭命令或关闭状态未得到完整确认，流程已按安全规则停止",
            "UVEX_SLIT_ILLUMINATION_ON_UNVERIFIED" => "狭缝定位 LED 的开启命令已经完成，但有界状态回读仍未确认开启；未采集任何 ON 帧",
            "NINA_EQUIPMENT_CONNECT_EXCEPTION" => "N.I.N.A. 设备连接阶段发生异常，尚未取得完整身份读回",
            "ATR_NOT_CONNECTED" or "ATR_CONNECT_FAILED" => "ATR585M 未连接或连接尝试失败，不能开始光谱曝光",
            "TELESCOPE_NOT_CONNECTED" or "TELESCOPE_CONNECT_FAILED" => "赤道仪未连接或连接尝试失败，不能转向或修正",
            "TELESCOPE_TRACKING_ENABLE_TIMEOUT" => "已请求恒星时跟踪，但在规定时间内没有得到启用读回",
            "TELESCOPE_TRACKING_ENABLE_FAILED" => "恒星时跟踪启用失败，目录转向尚未开始",
            "FINALIZE_INCOMPLETE" => "安全收尾仍有终态未确认；不会把本轮标记为完整结束",
            "ATR_TIER_NOT_SELECTED" => "ATR 探测曝光尚未选出合格档位，科学曝光不会开始",
            "ATR_SAVED_FRAME_TEMPERATURE_INVALID" => "光谱原始 FITS 的温度或保存后相机读回不合格；文件已保留，但未计入合格帧，后续曝光已停止",
            "HORIZON_BLOCKED" => "目标高度或预计运行时段触及本地地平线/围墙限制",
            "RAIN_DETECTED" => "安全链报告降雨，新的运动和曝光已被禁止",
            "REAL_PROFILE_DRIFT" => "当前 N.I.N.A. Profile 与本轮锁定的真实设备配置不一致",
            _ when effectiveCode.Contains("IDENTITY", StringComparison.Ordinal) => "设备或目标身份与本轮锁定证据不一致",
            _ when effectiveCode.Contains("HASH", StringComparison.Ordinal) || effectiveCode.Contains("FINGERPRINT", StringComparison.Ordinal) => "证据哈希或硬件指纹不一致，不能把当前数据当作本轮授权依据",
            _ when effectiveCode.Contains("WEATHER", StringComparison.Ordinal) || effectiveCode.Contains("SAFETY", StringComparison.Ordinal) => "天气或安全能力没有给出允许继续的可信状态",
            _ when HasCodeToken(effectiveCode, "ROOF", "COVER") => "屋顶或光路盖状态没有得到命令完成与读回确认",
            _ when effectiveCode.Contains("BUDGET", StringComparison.Ordinal) || effectiveCode.Contains("EXHAUSTED", StringComparison.Ordinal) => "本轮有界恢复的次数、位移或时间预算已经用尽",
            _ when effectiveCode.Contains("CALIBRATION", StringComparison.Ordinal) => "当前校准证据未通过适用于本阶段的质量门",
            _ when effectiveCode.Contains("PHD2", StringComparison.Ordinal) || effectiveCode.Contains("GUIDE", StringComparison.Ordinal) => "PHD2 导星或入缝证据未达到本阶段要求",
            _ when effectiveCode.Contains("G3", StringComparison.Ordinal) || effectiveCode.Contains("WCS", StringComparison.Ordinal) => "G3/WCS 证据不足以证明目标位置或授权下一步动作",
            _ when effectiveCode.Contains("QHY", StringComparison.Ordinal) || effectiveCode.Contains("PLATE", StringComparison.Ordinal) || effectiveCode.Contains("SOLVE", StringComparison.Ordinal) => "QHY/PL3 广域解算证据不足以继续定位",
            _ when effectiveCode.Contains("ATR", StringComparison.Ordinal) || effectiveCode.Contains("SCIENCE", StringComparison.Ordinal) => "ATR 光谱曝光或质量证据未达到本阶段要求",
            _ => "当前质量门未通过；程序没有把不确定状态当作成功",
        };

        var nested = !string.Equals(outerCode, effectiveCode, StringComparison.Ordinal)
            ? $"（内部代码 {effectiveCode}）"
            : string.Empty;
        if (ContainsCjk(raw) && !ContainsEnglishSentence(raw) &&
            string.Equals(description, "当前质量门未通过；程序没有把不确定状态当作成功", StringComparison.Ordinal))
        {
            return raw.Trim();
        }
        return $"{description}{nested}。";
    }

    private static string EnglishIssueSummary(string outerCode, string effectiveCode, string raw)
    {
        if (effectiveCode == "PHD2_SCIENCE_RECOVERY_BUDGET_UNAVAILABLE")
            return "Guiding evidence requires reacquisition, but the original fine-motion budget cannot authorize replacement. No fallback repositioning was started; saved spectra retain their quality flags.";
        if (!string.IsNullOrWhiteSpace(raw) && !ContainsCjk(raw)) return raw.Trim();
        var nested = !string.Equals(outerCode, effectiveCode, StringComparison.Ordinal)
            ? $" Inner code: {effectiveCode}."
            : string.Empty;
        return $"The quality gate did not pass; uncertainty was not treated as success.{nested}";
    }

    private static string Impact(ObservationStage stage, bool chinese) => (stage, chinese) switch
    {
        (ObservationStage.ValidateNightSetup, true) => "真实运行未获准进入设备动作阶段；不会因此启动曝光或移动机构。",
        (ObservationStage.SlewToCatalogTarget or ObservationStage.AcquireQhyWideField or ObservationStage.CoarseCenter, true) => "目标粗定位已停止；不会在缺少新鲜位置证据时继续发送新的转向命令。",
        (ObservationStage.AcquireG3SlitField or ObservationStage.PlaceTargetOnSlit or ObservationStage.StartGuiding, true) => "目标入缝/导星链已停止，新的 ATR 科学曝光不会开始；现有 FITS 和运动账本保留。",
        (ObservationStage.StartQhyPhotometry, true) => "QHY 同步测光没有启动或未获确认；主流程按本轮严格/可降级策略处理。",
        (ObservationStage.SelectAtrExposure or ObservationStage.RunScienceBlock, true) => "新的 ATR 科学曝光被禁止；已经完成的原始帧仍保留并带质量标记。",
        (ObservationStage.FinalizeObservation, true) => "至少一个收尾终态尚未确认，本轮不会被误报为完整安全结束。",
        (ObservationStage.ValidateNightSetup, false) => "The real run did not enter an action stage; no exposure or mechanism movement is authorized by this failure.",
        (ObservationStage.SlewToCatalogTarget or ObservationStage.AcquireQhyWideField or ObservationStage.CoarseCenter, false) => "Coarse acquisition stopped; no new slew is sent without fresh position evidence.",
        (ObservationStage.AcquireG3SlitField or ObservationStage.PlaceTargetOnSlit or ObservationStage.StartGuiding, false) => "Slit placement/guiding stopped and no new ATR science exposure can start; FITS evidence and motion ledgers are retained.",
        (ObservationStage.StartQhyPhotometry, false) => "Simultaneous QHY photometry did not start or was not confirmed; the run follows its strict/degraded policy.",
        (ObservationStage.SelectAtrExposure or ObservationStage.RunScienceBlock, false) => "New ATR science exposures are blocked; completed raw frames remain preserved and quality flagged.",
        (ObservationStage.FinalizeObservation, false) => "At least one cleanup terminal state is unconfirmed, so the run is not reported as safely complete.",
        _ => chinese ? "自动流程已在当前安全边界停止。" : "Automation stopped at the current safe boundary.",
    };

    private static string Recovery(ObservationStage stage, GateResult gate, bool chinese)
    {
        if (gate.Code == "G3_CATALOG_SHORT_POSITION_UNCONFIRMED" &&
            gate.Metrics?.TryGetValue("shortPositionFrames", out var frames) == true)
        {
            var used = frames.ToString("0", CultureInfo.InvariantCulture);
            return chinese
                ? $"本阶段已记录 {used}/{G3ShortPositionMeasurementPolicy.MaximumFrames} 张短曝光复核帧；单帧图像不足允许在此上限内补拍，多颗独立恒星歧义不会自动选星。此次仍未获可靠位置，已停止；未移动、未重新解算、未重置预算。"
                : $"This stage recorded {used}/{G3ShortPositionMeasurementPolicy.MaximumFrames} short confirmation frames; single-frame shape failures can retry within this cap. No reliable position was confirmed, so it stopped without motion, repeat solving or budget reset.";
        }
        var plan = ObservationAutomaticRecoveryPolicy.For(stage, gate);
        if (plan.IsRecoverable)
        {
            var action = (plan.Action, chinese) switch
            {
                (ObservationAutomaticRecoveryAction.RetrySameStage, true) => "重试当前阶段并重新读回状态",
                (ObservationAutomaticRecoveryAction.RetryWithFreshStageEvidence, true) => "丢弃派生结果并采集新的不可变证据",
                (ObservationAutomaticRecoveryAction.RebuildStageDependencies, true) => "从最早失效依赖开始重建定位/导星链",
                (ObservationAutomaticRecoveryAction.RetryTerminalCleanup, true) => "重复幂等的停止、关闭与终态核验",
                (ObservationAutomaticRecoveryAction.RetrySameStage, false) => "retry the same stage and re-read state",
                (ObservationAutomaticRecoveryAction.RetryWithFreshStageEvidence, false) => "discard derived results and acquire fresh immutable evidence",
                (ObservationAutomaticRecoveryAction.RebuildStageDependencies, false) => "rebuild the acquisition/guiding chain from its earliest stale dependency",
                (ObservationAutomaticRecoveryAction.RetryTerminalCleanup, false) => "repeat idempotent stop/close/terminal-state checks",
                _ => chinese ? "执行受限恢复" : "perform bounded recovery",
            };
            var exhausted = gate.Message.Contains("Automatic recovery", StringComparison.OrdinalIgnoreCase) &&
                            gate.Message.Contains("exhaust", StringComparison.OrdinalIgnoreCase);
            return (chinese, exhausted) switch
            {
                (true, true) => $"程序已在同一运行账本下{action}，允许的 {plan.MaximumAttempts} 次有界恢复已经用尽；没有重置运动或动作预算。",
                (true, false) => $"此代码允许程序在同一运行账本下{action}（最多 {plan.MaximumAttempts} 次），且不会重置运动或动作预算；技术时间线记录实际是否触发及次数。",
                (false, true) => $"The run attempted to {action} under the same ledger and exhausted the {plan.MaximumAttempts}-attempt bound without resetting motion/action budgets.",
                _ => $"This code permits the run to {action} under the same ledger (maximum {plan.MaximumAttempts}) without resetting motion/action budgets; the technical timeline records whether it actually ran.",
            };
        }

        var hardReason = HardStopReason(gate.Code, chinese);
        return chinese
            ? $"未自动重试：{hardReason}"
            : $"No automatic retry: {hardReason}";
    }

    private static string ShortPositionIssue(string raw)
    {
        var reason = raw switch
        {
            _ when raw.Contains("G3_SEP_RUNTIME_UNAVAILABLE", StringComparison.Ordinal) => "SEP 运行环境未就绪，请安装与插件版本匹配的图像检测环境",
            _ when raw.Contains("G3_SEP_CATALOG_REFERENCE_UNAVAILABLE", StringComparison.Ordinal) => "同一目录 ID 的标准坐标及自行未能取得；未使用分裂光斑猜测主星",
            _ when raw.Contains("G3_SEP_CATALOG_PRIMARY_UNMEASURED", StringComparison.Ordinal) => "完整星像尚未唯一对应标准目录主星位置；未用光翼碎片或伴星替代",
            _ when raw.Contains("G3_SEP_WORKER_FAILED", StringComparison.Ordinal) => "SEP 图像测量失败；没有回退到旧检测算法",
            _ when raw.Contains("G3_SEP_INPUT_CHANGED", StringComparison.Ordinal) => "SEP 程序或原始帧在测量期间发生变化",
            _ when raw.Contains("G3_SEP_INVALID_COVERAGE", StringComparison.Ordinal) => "SEP 候选列表不完整，不能据此确认目标唯一性",
            _ when raw.Contains("G3_SEP_UNMEASURED", StringComparison.Ordinal) => "SEP 尚未提取到信号与像素支撑充分的未饱和星像",
            _ when raw.Contains("G3_SEP_AMBIGUOUS", StringComparison.Ordinal) => "SEP 星像分量尚不能唯一关联到目录目标",
            _ when raw.Contains("G3_SHORT_UNSATURATED_UNMEASURED", StringComparison.Ordinal) => "尚未在未饱和短帧中测出符合轮廓和局部信噪比要求的星像",
            _ when raw.Contains("G3_SHORT_UNSATURATED_AMBIGUOUS", StringComparison.Ordinal) => "短帧中仍有多颗独立恒星候选，不能唯一对应目标",
            _ when raw.Contains("G3_SHORT_CONTOUR_BLENDED", StringComparison.Ordinal) => "内外层轮廓尺寸或长短轴比异常，不能确认独立星点",
            _ when raw.Contains("G3_SHORT_CONTOURS_DISAGREE", StringComparison.Ordinal) => "同一短帧的多层轮廓中心不一致",
            _ when raw.Contains("G3_SHORT_REPEAT_DISAGREES", StringComparison.Ordinal) => "不同有效短帧的位置差超过一致性上限",
            _ when raw.Contains("G3_SHORT_CATALOG_PRIMARY_NOT_MEASURED", StringComparison.Ordinal) => "本帧尚未测到目录配对确认的同一主星；不会用伴星替代目标",
            _ when raw.Contains("G3_SHORT_REPEAT_REQUIRED", StringComparison.Ordinal) => "尚未取得第二张位置一致的有效短帧",
            _ when raw.Contains("G3_SHORT_WINGS_INCOMPLETE", StringComparison.Ordinal) => "未饱和内翼的覆盖或信噪比不足",
            _ when raw.Contains("G3_SHORT_CORE_TOO_LARGE", StringComparison.Ordinal) => "短帧削顶区域过大，仍不能可靠定位星核",
            _ when raw.Contains("G3_SHORT_CORE_NOT_UNIQUE", StringComparison.Ordinal) => "短帧中没有唯一可识别的实心星核",
            _ when raw.Contains("G3_SHORT_FRAME_REUSED", StringComparison.Ordinal) => "收到重复图像或非递增拍摄时间，不能作为独立新帧",
            _ when raw.Contains("G3_SHORT_POSITION_OUTSIDE", StringComparison.Ordinal) => "实测位置连同分散范围超出本轮目标识别窗口",
            _ => "低增益短曝光位置复核未通过；请查看具体轮廓、新帧位置和绑定证据",
        };
        return $"WCS 已确认目标在视场内，但{reason}；尚未授权后续定位";
    }

    private static string HardStopReason(string code, bool chinese)
    {
        if (code.StartsWith("PHD2_GUIDE_OUTPUT_", StringComparison.Ordinal))
            return chinese
                ? "这是导星输出链故障，不是精度警告；未发出精调时最多自动重连一次，已有精调则保留未结责任。详见本轮停止/重连证据，不以重复粗定位代替修复连接。"
                : "This is a guide-output failure, not a precision warning. One reconnect is permitted before exact-lock motion; outstanding motion responsibility is retained. Stop/reconnect evidence records the actual outcome; repeated coarse centering is not connection repair.";
        if (code is Phd2StageFailurePolicy.FrameTimeout or Phd2StageFailurePolicy.StatusTimeout)
            return chinese
                ? "当前缺少新的取图或所有者状态证据；需先确认停止并恢复 PHD2/相机连接，随后重新定位，不能复用旧帧或在曝光中重启设备。"
                : "fresh frame or owner-state evidence is missing; confirm stopped owners and restore PHD2/camera connectivity before reacquisition. Do not reuse an old frame or restart equipment during an exposure.";
        if (code.Contains("SAFETY", StringComparison.Ordinal) ||
            code.Contains("RAIN", StringComparison.Ordinal) ||
            code.Contains("HORIZON", StringComparison.Ordinal) ||
            HasCodeToken(code, "ROOF", "COVER"))
        {
            return chinese ? "这是安全/环境硬门，等待条件真实改变后再复核。" : "this is a safety/environment hard gate and requires a real state change before revalidation.";
        }
        if (code.Contains("IDENTITY", StringComparison.Ordinal) ||
            code.Contains("HASH", StringComparison.Ordinal) ||
            code.Contains("FINGERPRINT", StringComparison.Ordinal) ||
            code.Contains("TOPOLOGY", StringComparison.Ordinal))
        {
            return chinese ? "身份、哈希或拓扑不一致不能靠重复读取消除。" : "identity, hash or topology mismatches cannot be cleared by repeated reads.";
        }
        if (code.Contains("BUDGET", StringComparison.Ordinal) ||
            code.Contains("EXHAUSTED", StringComparison.Ordinal) ||
            code.Contains("RETURN", StringComparison.Ordinal))
        {
            return chinese ? "动作/回程/时间预算或物理位置责任必须保留，不能重新发放预算。" : "motion, return and time budgets or physical-position accountability must be preserved and cannot be reissued.";
        }
        return chinese ? "此代码没有经过审核的幂等恢复路径，重复执行可能扩大设备状态不确定性。" : "this code has no reviewed idempotent recovery path; retrying could enlarge equipment-state uncertainty.";
    }

    private static string Recommendation(ObservationStage stage, string outerCode, string effectiveCode, bool chinese)
    {
        var code = effectiveCode;
        if (code is "G3_MOTION_CRASH_RETURN_BLOCKED" or "G3_MOTION_CROSS_PIER_ORIGIN_UNCONFIRMED")
            return chinese
                ? "查看上轮回程原始原因，以及当前侧别、起点残差和读回漂移。新一轮启动只在最后绝对回程已稳定到位时自动结束旧记录；否则需要人工确认设备位置并选择恢复方式，不能靠重启或删除账本绕过，也不需要为此重调星点检测。"
                : "Inspect the prior return cause, current pier side, origin residual and readback drift. A new run closes only a final absolute return already proven stable at its origin. Otherwise confirm the physical position and choose recovery explicitly; restarting, deleting the ledger or retuning star detection is not a position proof.";
        if (code.StartsWith("PHD2_GUIDE_OUTPUT_", StringComparison.Ordinal))
            return chinese
                ? "检查 PHD2 原生导星日志的修正请求与 RA/DEC 脉冲时长，以及 ASCOM/RPC 错误；在无曝光、导星已停止的边界恢复原设备连接。存在未结精调时先核验其责任，不能只清状态或放宽导星精度。"
                : "Compare requested corrections with native RA/DEC pulse durations and inspect ASCOM/RPC errors. Restore the bound equipment only with exposures idle and guiding stopped. Reconcile outstanding lock motion; do not merely clear state or relax precision.";
        if (code is Phd2StageFailurePolicy.FrameTimeout or Phd2StageFailurePolicy.StatusTimeout)
            return chinese
                ? "检查同一时刻的 PHD2 原始日志、相机曝光超时/重连记录和 USB/供电。先结束本轮并确认停止，再由 PHD2 唯一所有者恢复连接；从前台重新定位、找缝、导星后重拍。旧 FITS 和运动账本保留，单凭超时不能归因于云或相机厂商。"
                : "Inspect the matching PHD2 debug log, camera capture/reconnect failures and USB/power. End this run and confirm stopped owners before restoring the camera through PHD2, then reacquire through the frontend. Preserve FITS and motion ledgers; a timeout alone does not identify cloud or a camera-vendor defect.";
        if (code == "PHD2_NATIVE_GUIDE_GEOMETRY_REJECTED" || code == "PHD2_OFF_SLIT_NATIVE_SELECTION_EXHAUSTED")
        {
            return chinese
                ? "查看 G3 导星选星帧和每次拒绝的边缘/目标晕/狭缝距离。程序会先做有界重新选星；耗尽后只能使用已明确启用的有人监督直导目标分支，或人工调整构图。"
                : "Inspect the G3 guide-selection frame and the edge/target-halo/slit distances for each rejection. Bounded reselection runs first; after exhaustion, use only an explicitly enabled supervised direct-target route or adjust framing manually.";
        }
        if (code.Contains("CALIBRATION", StringComparison.Ordinal))
        {
            return chinese
                ? "查看 PHD2 当前 active calibration 的轴速率、正交性、奇偶性、pier side、时间戳和 post-settle 残差。程序最多执行一次强制重新校准；仍失败时不要反复校准，先检查赤道仪/导星连接和参数。"
                : "Inspect the active PHD2 calibration axis rates, orthogonality, parity, pier side, timestamp and post-settle residual. At most one forced recalibration is attempted; if it still fails, inspect the mount/guider connection and parameters instead of recalibrating repeatedly.";
        }
        if (code.Contains("G3", StringComparison.Ordinal) || code.Contains("SLIT", StringComparison.Ordinal) || code.Contains("PHD2", StringComparison.Ordinal))
        {
            return chinese
                ? "打开本轮 G3/PHD2 证据，核对目标质心、LED 差分狭缝、WCS、导星星与新鲜残差；不要用旧预览代替本次 FITS。"
                : "Open this run's G3/PHD2 evidence and check the target centroid, LED-difference slit, WCS, guide star and fresh residual; do not substitute an old preview for the current FITS.";
        }
        if (code.Contains("QHY", StringComparison.Ordinal) || code.Contains("WCS", StringComparison.Ordinal) || code.Contains("SOLVE", StringComparison.Ordinal))
        {
            return chinese
                ? "打开 QHY/GS350 原始 FITS 和 PL3 sidecar，核对滤镜读回、真实星形、云层、WCS 与赤道仪读回时间；需要时只重新采集新帧，不手工伪造坐标证据。"
                : "Open the QHY/GS350 raw FITS and PL3 sidecar; check filter readback, real star shapes, cloud, WCS and mount-readback time. Reacquire a fresh frame if needed; never fabricate coordinate evidence.";
        }
        if (stage is ObservationStage.SelectAtrExposure or ObservationStage.RunScienceBlock)
        {
            return chinese
                ? "打开 ATR 二维/一维预览与原始 FITS，核对 ROI、饱和、谱线位置、SNR、制冷和导星质量；失败帧保留但不计入合格科学帧。"
                : "Open the ATR 2D/1D preview and raw FITS; check ROI, saturation, line position, SNR, cooling and guide quality. Failed frames remain preserved but are not counted as accepted science frames.";
        }
        return chinese
            ? "先核对错误代码、质量数值和本轮时间线，再打开最近证据。点击“恢复复核”只会重新检查可能过期的门，不会绕过安全、身份或运动预算。"
            : "Review the code, quality values and this run's timeline, then open the latest evidence. Resume/revalidate only rechecks stale gates; it does not bypass safety, identity or motion budgets.";
    }

    private static string EffectiveCode(string outerCode, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || outerCode is not (
                "STAGE_EXCEPTION" or
                "PHD2_SLIT_PLACEMENT_FAILED_SAFE")) return outerCode;
        var candidates = MachineCodeRegex().Matches(raw)
            .Select(match => match.Value)
            .Where(code => !string.Equals(code, outerCode, StringComparison.Ordinal) &&
                           code is not "NINA" and not "PHD" and not "WCS" and not "FITS")
            .ToArray();
        return candidates.Length == 0 ? outerCode : candidates[^1];
    }

    private static bool HasCodeToken(string code, params string[] expectedTokens)
    {
        var tokens = code.Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return expectedTokens.Any(expected =>
            tokens.Any(token => string.Equals(token, expected, StringComparison.Ordinal)));
    }

    private static string DisplayPassedMessage(GateResult gate, bool chinese)
    {
        if (!chinese) return string.IsNullOrWhiteSpace(gate.Message) ? gate.Code : gate.Message.Trim();
        if (ContainsCjk(gate.Message) && !ContainsEnglishSentence(gate.Message)) return gate.Message.Trim();
        return gate.Severity == GateSeverity.Warning
            ? $"质量门 {gate.Code} 给出警告，但允许本轮继续。"
            : $"质量门 {gate.Code} 已通过。";
    }

    private static bool ContainsEnglishSentence(string value) =>
        EnglishSentenceRegex().IsMatch(value);

    private static string NormalizeChineseTerminology(string value) => value
        .Replace("active calibration", "当前使用的校准", StringComparison.OrdinalIgnoreCase)
        .Replace("PostSettle", "稳定后", StringComparison.OrdinalIgnoreCase)
        .Replace("fresh residual", "新采残差", StringComparison.OrdinalIgnoreCase)
        .Replace("commissioning preset", "设备标定方案", StringComparison.OrdinalIgnoreCase)
        .Replace("Candidate", "候选", StringComparison.Ordinal)
        .Replace("warning", "警告", StringComparison.OrdinalIgnoreCase)
        .Replace("exact-lock", "精确锁点", StringComparison.OrdinalIgnoreCase)
        .Replace("Mount span", "赤道仪位移跨度", StringComparison.OrdinalIgnoreCase)
        .Replace("steps", "步", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex("\\b[A-Z][A-Z0-9]+(?:_[A-Z0-9]+){1,}\\b", RegexOptions.CultureInvariant)]
    private static partial Regex MachineCodeRegex();

    [GeneratedRegex("(?:[A-Za-z][A-Za-z0-9'./+-]*\\s+){4,}[A-Za-z][A-Za-z0-9'./+-]*", RegexOptions.CultureInvariant)]
    private static partial Regex EnglishSentenceRegex();
}
