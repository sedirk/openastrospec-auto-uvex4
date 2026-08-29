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
    private static readonly IReadOnlyDictionary<string, string> ChineseMetricNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["attempt"] = "尝试次数",
            ["attempts"] = "尝试次数",
            ["searchAttempts"] = "搜索次数",
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
        IsChinese(culture)
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
            };

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
                passedSummary,
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
            ? ChineseIssueSummary(gate.Code, effectiveCode, gate.Message)
            : EnglishIssueSummary(gate.Code, effectiveCode, gate.Message);
        var impact = Impact(stage, chinese);
        var recovery = Recovery(stage, gate, chinese);
        var recommendation = Recommendation(stage, gate.Code, effectiveCode, chinese);
        var technical = string.IsNullOrWhiteSpace(gate.Message)
            ? gate.Code
            : $"{gate.Code}: {gate.Message.Trim()}";
        return new ObservationIssuePresentation(summary, impact, recovery, recommendation, technical);
    }

    public static string EventMessage(
        ObservationStage? stage,
        string? code,
        string? rawMessage,
        CultureInfo? culture = null,
        bool isAttention = false)
    {
        var message = rawMessage?.Trim() ?? string.Empty;
        if (!IsChinese(culture)) return message;
        if (string.IsNullOrWhiteSpace(code))
        {
            if (ContainsCjk(message) && !ContainsEnglishSentence(message)) return message;
            return "运行组件返回了未分类的技术状态；原始消息已保留在时间线技术详情中。";
        }

        var resolvedStage = stage ?? ObservationStage.ValidateNightSetup;
        if (isAttention)
        {
            return Present(resolvedStage, GateResult.Unknown(code, message), culture).Summary;
        }

        if (ContainsCjk(message) && !ContainsEnglishSentence(message)) return message;
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
        return kind switch
        {
            "g3-slit-illumination-sequence" => "G3 狭缝照明序列",
            "g3-bounded-search-summary" => "G3 有界搜索摘要",
            "g3-plate-solve-frame" => "G3 解算帧",
            "g3-phd2-guide-selection-frame" => "PHD2/G3 导星选星帧",
            "phd2-lock-shift-fresh-residual" => "PHD2 入缝新鲜残差",
            "phd2-full-frame-guide-takeover" => "PHD2 全幅导星接管",
            "qhy-mount-coordinate-sync-intent" => "QHY WCS 坐标同步意图",
            "qhy-acquisition-frame" => "QHY 广域采集帧",
            "atr-probe" => "ATR 探测曝光",
            _ => $"其他运行证据（{kind}）",
        };
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

        if (IsChinese(culture))
        {
            return ContainsEnglishSentence(raw)
                ? new ObservationUiOperationError("界面操作状态已更新；原始技术详情可展开查看。", raw)
                : new ObservationUiOperationError(NormalizeChineseTerminology(raw), string.Empty);
        }

        if (!ContainsCjk(raw)) return new ObservationUiOperationError(raw, string.Empty);
        var translated = ObservationStaticTextCatalog.Translate(raw, culture);
        return new ObservationUiOperationError(translated, raw);
    }

    private static string ChineseIssueSummary(string outerCode, string effectiveCode, string raw)
    {
        var description = effectiveCode switch
        {
            "STAGE_EXCEPTION" => "当前阶段发生未分类异常，流程已在动作边界停止",
            "PHD2_SLIT_PLACEMENT_FAILED_SAFE" => "PHD2 入缝没有取得可验证结果，流程已安全停止",
            "PHD2_NATIVE_GUIDE_GEOMETRY_REJECTED" => "PHD2 选中的导星星撞到探测器边缘、目标光晕或狭缝保护区",
            "PHD2_OFF_SLIT_NATIVE_SELECTION_EXHAUSTED" => "PHD2 已用完旁星有界重选次数，仍未找到满足几何限制的导星星",
            "G3_FRAME_REUSED" => "精调阶段拒绝复用旧 G3 残差帧；每次位移后都必须取得新帧",
            "G3_CATALOG_WCS_AUTHORITY_INVALID" => "目录/WCS 目标几何证据格式无效，不能据此授权入缝位移",
            "PHD2_PLACEMENT_SETTLE_STALE" => "入缝后保存的 PHD2 导星或稳定纪元已变化，旧证据不能继续使用",
            "PHD2_CALIBRATION_PRE_GUIDE_REJECTED" => "当前 PHD2 校准未通过导星前质量门，未发送导星命令",
            "PHD2_RECALIBRATION_DID_NOT_BECOME_ACTIVE" => "已执行一次强制校准，但 PHD2 仍未报告可用的当前校准",
            "PHD2_POST_CALIBRATION_SETTLE_FAILED" => "PHD2 重新校准后未在规定时间内取得稳定证据",
            "PHD2_GUIDING_SUPERVISED_ONLY" => "当前 PHD2 校准只允许有人监督运行，未授予无人值守科学曝光权限",
            "GUIDING_LOST" => "PHD2 已确认脱锁，新的科学曝光已被禁止",
            "GUIDING_UNSTABLE" or "GUIDING_NOT_STABLE" => "PHD2 仍在导星，但稳定性未达到本轮科学曝光门限",
            "G3_BOUNDED_SEARCH_EXHAUSTED_RETURNED" => "G3 邻场搜索未找到可接受目标，赤道仪已返回保存的搜索起点",
            "G3_FIELD_MOUNT_BINDING_STALE" => "G3 图像与赤道仪读回的时间绑定已过期，旧帧不能授权运动",
            "G3_FIELD_MOUNT_BINDING_FRAME_MISSING" => "G3 运动证据所引用的原始帧不存在",
            "G3_FIELD_MOUNT_BINDING_FRAME_UNREADABLE" => "G3 运动证据所引用的原始帧无法读取",
            "G3_FIELD_MOUNT_BINDING_READBACK_UNAVAILABLE" => "拍摄 G3 帧时没有取得可信的赤道仪位置读回",
            "G3_PLATE_SOLVE_LADDER_TRANSIENT_EXHAUSTED" => "G3 解算曝光阶梯遇到连续临时故障，本轮新鲜阶梯已经用尽",
            "G3_CLOUD_OR_TRANSPARENCY_INVALID" => "G3 新帧没有足够的一致星点，当前更像云层或透明度突变而不是位置错误",
            "BRIGHT_TARGET_ONLY_ANNULAR_GHOSTS" => "亮目标分析只找到空心环状鬼影，没有找到可作为真目标的实心核心",
            "BRIGHT_TARGET_TOPOLOGY_UNPROVEN" => "亮目标与鬼影的拓扑关系仍不唯一，目标身份尚未证明",
            "SLIT_LED_IDENTITY_GEOMETRY_UNAVAILABLE" => "狭缝灯差分图未能恢复可信的物理狭缝几何",
            "QHY_MOUNT_COORDINATE_SYNC_READBACK_FAILED" => "QHY WCS 同步赤道仪坐标后，读回残差仍超过允许值；没有发送 Slew",
            "QHY_SOLVE_REQUIRED" => "尚无可接受的 QHY/PL3 广域解算，不能进入后续粗定位",
            "QHY_COARSE_SOURCE_FRAME_MISSING" or "QHY_NATIVE_CENTER_SOURCE_FRAME_MISSING" => "粗定位所需的 QHY 原始帧缺失",
            "QHY_NATIVE_CENTER_FRESH_SOURCE_REQUIRED" => "粗定位只拿到旧 QHY 帧，必须重新采集新鲜见证帧",
            "UVEX_NOT_READY" => "UVEX4 服务未返回 Ready 和可信位置，机构动作被禁止",
            "UVEX_AUTO_CONNECT_FAILED" => "未能通过固定 COM5 配置连接并验证 UVEX4",
            "NINA_EQUIPMENT_CONNECT_EXCEPTION" => "N.I.N.A. 设备连接阶段发生异常，尚未取得完整身份读回",
            "ATR_NOT_CONNECTED" or "ATR_CONNECT_FAILED" => "ATR585M 未连接或连接尝试失败，不能开始光谱曝光",
            "TELESCOPE_NOT_CONNECTED" or "TELESCOPE_CONNECT_FAILED" => "赤道仪未连接或连接尝试失败，不能转向或修正",
            "TELESCOPE_TRACKING_ENABLE_TIMEOUT" => "已请求恒星时跟踪，但在规定时间内没有得到启用读回",
            "TELESCOPE_TRACKING_ENABLE_FAILED" => "恒星时跟踪启用失败，目录转向尚未开始",
            "FINALIZE_INCOMPLETE" => "安全收尾仍有终态未确认；不会把本轮标记为完整结束",
            "ATR_TIER_NOT_SELECTED" => "ATR 探测曝光尚未选出合格档位，科学曝光不会开始",
            "HORIZON_BLOCKED" => "目标高度或预计运行时段触及本地地平线/围墙限制",
            "RAIN_DETECTED" => "安全链报告降雨，新的运动和曝光已被禁止",
            "REAL_PROFILE_DRIFT" => "当前 N.I.N.A. Profile 与本轮锁定的真实设备配置不一致",
            _ when effectiveCode.Contains("IDENTITY", StringComparison.Ordinal) => "设备或目标身份与本轮锁定证据不一致",
            _ when effectiveCode.Contains("HASH", StringComparison.Ordinal) || effectiveCode.Contains("FINGERPRINT", StringComparison.Ordinal) => "证据哈希或硬件指纹不一致，不能把当前数据当作本轮授权依据",
            _ when effectiveCode.Contains("WEATHER", StringComparison.Ordinal) || effectiveCode.Contains("SAFETY", StringComparison.Ordinal) => "天气或安全能力没有给出允许继续的可信状态",
            _ when effectiveCode.Contains("ROOF", StringComparison.Ordinal) || effectiveCode.Contains("COVER", StringComparison.Ordinal) => "屋顶或光路盖状态没有得到命令完成与读回确认",
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

    private static string HardStopReason(string code, bool chinese)
    {
        if (code.Contains("SAFETY", StringComparison.Ordinal) ||
            code.Contains("RAIN", StringComparison.Ordinal) ||
            code.Contains("HORIZON", StringComparison.Ordinal) ||
            code.Contains("ROOF", StringComparison.Ordinal))
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
