using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

public enum SpectroscopyTargetFailurePolicy { EndNight, SkipQualityFailure }

public sealed record SpectroscopyFailurePolicyChoice(SpectroscopyTargetFailurePolicy Value, string ChineseLabel)
{
    public string Label => ObservationStaticTextLocalization.Translate(ChineseLabel,
        ObservationStaticTextLocalization.EffectiveCulture);

    public static IReadOnlyList<SpectroscopyFailurePolicyChoice> Choices { get; } =
    [
        new(SpectroscopyTargetFailurePolicy.EndNight, "结束本夜并收口"),
        new(SpectroscopyTargetFailurePolicy.SkipQualityFailure, "质量失败且停止确认后跳过"),
    ];
}

/// <summary>Saved, per-target acquisition choices; never writes the shared Profile.</summary>
internal sealed record NativeSequencePlan(
    int ScienceFrames, int MaximumAttempts, double FixedExposureSeconds,
    bool DeferObservatoryCloseout = false, DateTimeOffset? DeadlineUtc = null,
    bool StopAtDawn = false, double DawnSunAltitudeDegrees = -12,
    double LatitudeDegrees = 0, double LongitudeDegrees = 0,
    bool HomeBeforeParkOnFinalize = false, int MountHomeTimeoutSeconds = 180,
    double InitialGuideSettlePixels = 0, int InitialGuideStableSeconds = 5,
    int InitialGuideTimeoutSeconds = 180, bool ForceGuideCalibration = false,
    bool AutomaticGuideRecovery = true, int GuideStarWaitSeconds = 300)
{
    internal UvexAdv.Phd2.Phd2SettleCriteria InitialGuideCriteria(UvexAdv.Phd2.Phd2SettleCriteria configured) =>
        InitialGuideSettlePixels > 0
            ? new(InitialGuideSettlePixels, InitialGuideStableSeconds, InitialGuideTimeoutSeconds)
            : configured;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (GuideStarWaitSeconds is < 30 or > 600) errors.Add("导星星点恢复等待须为 30–600 秒。");
        if (MountHomeTimeoutSeconds is < 30 or > 600) errors.Add("回零超时须为 30–600 秒。");
        if (!double.IsFinite(InitialGuideSettlePixels) || InitialGuideSettlePixels < 0 || InitialGuideSettlePixels > 5)
            errors.Add("初始导星稳定半径须为 0（沿用 Profile）至 5 px。");
        if (InitialGuideStableSeconds is < 1 or > 30 || InitialGuideTimeoutSeconds is < 30 or > 300)
            errors.Add("初始导星稳定时长须为 1–30 秒，等待上限须为 30–300 秒。");
        if (ScienceFrames is < 1 or > 10000) errors.Add("科学帧数须为 1–10000。");
        if (MaximumAttempts < ScienceFrames || MaximumAttempts > 20000) errors.Add("最多尝试次数须不少于科学帧数且不超过 20000。");
        if (!double.IsFinite(FixedExposureSeconds) || FixedExposureSeconds < 0 || FixedExposureSeconds > 3600)
            errors.Add("固定曝光须为 0（自动探针选档）或 0–3600 秒。");
        if (!double.IsFinite(DawnSunAltitudeDegrees) || DawnSunAltitudeDegrees is < -18 or > 0)
            errors.Add("晨光停止太阳高度须为 −18° 至 0°。");
        if (!double.IsFinite(LatitudeDegrees) || Math.Abs(LatitudeDegrees) > 90 ||
            !double.IsFinite(LongitudeDegrees) || Math.Abs(LongitudeDegrees) > 180) errors.Add("站点经纬度无效。");
        return errors;
    }

    // New operations declare their scope. The code list remains for legacy
    // producers and old recorded runs, not as the sole next-target contract.
    internal static bool IsSkippableQualityFailure(GateResult? gate) =>
        gate is { Disposition: not GateDisposition.Passed } &&
        (gate.FailureScope == GateFailureScope.TargetQuality || IsSkippableQualityFailure(gate.Code));

    // An explicit allow-list, never an exception-text or 'not safety' heuristic.
    // Post-WCS image confirmation failures reject this target, not the whole
    // night. Confirmed coarse-return exhaustion/non-convergence is likewise a
    // target outcome, not a night failure. The caller must still independently
    // prove idle and settled motion; a RETURNED suffix alone grants nothing.
    internal static bool IsSkippableQualityFailure(string? code) => code is
        "G3_CATALOG_SHORT_POSITION_UNCONFIRMED" or "G3_BOUNDED_SEARCH_EXHAUSTED_RETURNED" or
        // No search command fits, or none was sent before an attested return.
        // Independent target-boundary idle/ledger retirement is still required.
        "G3_SEARCH_DURABLE_RESERVE_INVALID" or "G3_SEARCH_NOT_STARTED_RETURNED" or
        G3WcsRecoveryPolicy.ExhaustedReturnedCode or G3WcsRecoveryPolicy.ResponseNotConvergedCode or
        "G3_POST_WCS_SHORT_UNCONFIRMED" or G3PostWcsShortHandoffPolicy.OutsideCode or
        "G3_POST_WCS_TARGET_NOT_MEASURED" or "ATR_ATTEMPT_LIMIT" or
        "ATR_PROBE_ATTEMPT_LIMIT" or HorizonEvaluation.WindowExhaustedCode or
        Phd2DependencyRebuildStopPolicy.InitialSettleNotReachedCode or
        Phd2PlacementGuideWindowPolicy.LegacyPreMotionRejectedCode or
        AdaptiveGuidingRecovery.ExhaustedCode or AdaptiveGuidingRecovery.StarsTimeoutCode or
        "PHD2_LOCK_RETURN_EPOCH_CHANGED" or "PHD2_LOCK_RETURN_SETTLE_FAILED" or
        // Arrival may remain unproven after ADR-0027 checked-stop retirement.
        // This classifies the failed target, not permission to skip idle/ledger checks.
        "PHD2_LOCK_RETURN_POSITION_UNCONFIRMED" or
        "PHD2_SLIT_COMPLETION_WINDOW_EXHAUSTED_RETURNED" or "PHD2_PLACEMENT_GUIDE_STAR_LOST" or
        "PHD2_LOCK_INHERITED_BUDGET_EXHAUSTED" or "G3_MOTION_TIME_LIMIT";

    // Scheduling rejection before a runner, coordinator, or device action exists.
    // This is not a relaxed runtime horizon gate or proof that hardware is idle.
    internal static GateResult? PreflightHorizonSkip(ObservationPlan plan, SpectroscopyTargetFailurePolicy policy)
    {
        if (policy != SpectroscopyTargetFailurePolicy.SkipQualityFailure || plan.Validate().Count != 0)
            return null;
        var gate = HorizonCalculator.Evaluate(plan).ToGateResult();
        return gate.Disposition == GateDisposition.Passed ? null : gate;
    }

    internal GateResult? StopGate(DateTimeOffset now, Func<DateTimeOffset, double> sunAltitude)
    {
        if (DeadlineUtc is { } end && now >= end)
            return GateResult.Unknown("NIGHT_DEADLINE_REACHED", "本夜截止时间已到；保留已保存帧，结束本夜并执行已配置收口。");
        if (StopAtDawn && sunAltitude(now) >= DawnSunAltitudeDegrees)
            return GateResult.Unknown("NIGHT_DAWN_REACHED", "太阳已达到本夜晨光停止高度；保留已保存帧，结束本夜并执行已配置收口。");
        return null;
    }

    internal GateResult? StopGateNow() => StopGate(DateTimeOffset.UtcNow, time =>
        NINA.Astrometry.AstroUtil.GetSunAltitude(time.UtcDateTime, new NINA.Astrometry.ObserverInfo
        { Latitude = LatitudeDegrees, Longitude = LongitudeDegrees }));
}
