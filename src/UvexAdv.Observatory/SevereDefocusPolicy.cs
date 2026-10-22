namespace UvexAdv.Observatory;

/// <summary>
/// A repeated, field-wide blur candidate, not proof of defocus or motion authority.
/// Both immutable inputs must be fresh frames at the same exposure/gain/binning,
/// with the second measured against the first frame's fixed SEP reference.
/// The caller owns freshness, equipment/safety checks and the original budgets.
/// </summary>
public sealed record SevereDefocusDecision(bool ShouldAttemptFocus, GateResult Gate, int[] StarIds);

public static class SevereDefocusPolicy
{
    public const string Version = "SEP-severe-defocus-candidate-v1";
    // Deliberately severe, not a normal observing-quality threshold. Read-only
    // current-worker replays of baseline pairs from the 2026-09-14 verified-
    // origin run and 10-08 retained-origin run give median R50 about 4.8–6.0 px
    // and R80 about 8.5–11.1 px. Those are baseline frames, not new focus-success
    // claims. BOTH 10/15 px are exploratory extreme-blur limits outside that
    // scale, not a measured optimal classifier or 25 px aperture fractions.
    // These pixel limits apply only to the existing bin-1, 25 px SEP aperture;
    // they are not a universal telescope calibration or a scientific hard stop.
    public const double SevereR50Pixels = 10;
    public const double SevereR80Pixels = 15;
    private const int MinimumStars = 3;
    private const double MinimumCommonFraction = .75;
    // Reuse the focus worker's SNR floor; no requirement that an astigmatic C11
    // star be round. Only obvious >2:1 elongation is ineligible for this
    // exploratory candidate test. The scan/ABAB still decides optical benefit.
    private const double MinimumSignalToNoise = 15;
    private const double MinimumAxisRatio = .5;
    private const double MaximumFluxRatio = 1.25;
    private const double MaximumRadiusRatio = 1.20;
    private const double MaximumTranslationPixels = 5;
    private const double MaximumTranslationResidualPixels = 2;

    public static SevereDefocusDecision Evaluate(SepFocusFrame first, SepFocusFrame second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        var metrics = new Dictionary<string, double>
        {
            ["policyVersion"] = 1,
            ["severeR50Pixels"] = SevereR50Pixels,
            ["severeR80Pixels"] = SevereR80Pixels,
            ["minimumCommonFraction"] = MinimumCommonFraction,
            ["minimumSignalToNoise"] = MinimumSignalToNoise,
            ["minimumAxisRatio"] = MinimumAxisRatio,
            ["maximumAllowedFluxRatio"] = MaximumFluxRatio,
            ["maximumAllowedRadiusRatio"] = MaximumRadiusRatio,
            ["maximumAllowedTranslationPixels"] = MaximumTranslationPixels,
            ["maximumAllowedTranslationResidualPixels"] = MaximumTranslationResidualPixels,
        };
        SevereDefocusDecision Reject(string code, string message) => new(false,
            GateResult.Warn(code, message + " 未授权自动对焦；这不是新的观测阻断。", metrics), []);
        if (!ValidFrame(first) || !ValidFrame(second))
            return Reject("G3_SEVERE_DEFOCUS_INVALID_SAMPLES", "严重离焦复核缺少有效的 SEP 固定孔径新帧。");
        if (!first.Reference.SequenceEqual(second.Reference))
            return Reject("G3_SEVERE_DEFOCUS_REFERENCE_CHANGED", "两帧不是同一固定参考星群，不能拼接焦点诊断。");

        var current = second.Stars.ToDictionary(s => s.Id);
        var common = first.Stars.Where(s => current.ContainsKey(s.Id)).Select(s => (A: s, B: current[s.Id])).ToArray();
        metrics["commonMeasuredStars"] = common.Length;

        // Legacy focus records remain usable by the manual runner, but absent
        // shape/aperture evidence cannot authorize automatic recovery. The
        // worker already excludes saturation, true neighbours and aperture edges.
        if (common.Length > 0 && common.All(p => !HasShape(p.A) || !HasShape(p.B)))
            return Reject("G3_SEVERE_DEFOCUS_SHAPE_UNAVAILABLE", "缺少形态/固定 25 像素孔径证据，不能以旧协议半径自动调焦。");

        // Choose the fixed same-star cohort by identity, signal and shape
        // BEFORE looking at blur radii. One marginal unrelated star must not
        // veto three valid stars; selecting only the broadest stars is forbidden.
        var pairs = common.Where(p => Eligible(p.A) && Eligible(p.B)).ToArray();
        var union = first.Stars.Where(Eligible).Select(s => s.Id)
            .Union(second.Stars.Where(Eligible).Select(s => s.Id)).Count();
        metrics["eligibleUnionStars"] = union;
        metrics["selectedCohortStars"] = pairs.Length;
        metrics["excludedCommonStars"] = common.Length - pairs.Length;
        metrics["commonFraction"] = union == 0 ? 0 : pairs.Length / (double)union;
        if (pairs.Length < MinimumStars || metrics["commonFraction"] < MinimumCommonFraction)
            return Reject("G3_SEVERE_DEFOCUS_STARS_INSUFFICIENT", "按同星身份、信噪比和非明显拖尾形态选取的固定星群不足三颗或重复覆盖不足；不能确认严重宽化。");

        // Three knots of one donut or one ghost family near a bright core must
        // not count as three independent stars. Isolation is checked in BOTH
        // measurements in addition to the worker's aperture exclusion.
        double span = 0;
        for (var i = 0; i < pairs.Length; i++)
        for (var j = i + 1; j < pairs.Length; j++)
        {
            var a = Distance(pairs[i].A, pairs[j].A);
            var b = Distance(pairs[i].B, pairs[j].B);
            if (a < 50 || b < 50)
                return Reject("G3_SEVERE_DEFOCUS_STARS_NOT_INDEPENDENT", "星点间距不足独立完整孔径，不能以一个分瓣目标或局部鬼影触发调焦。");
            span = Math.Max(span, Math.Min(a, b));
        }
        metrics["ensembleSpanPixels"] = span;
        if (span < 100)
            return Reject("G3_SEVERE_DEFOCUS_FIELD_COVERAGE_LOW", "星群仅覆盖局部区域，不能证明持续的全场模糊。");

        var dx = Median(pairs.Select(p => p.B.X - p.A.X));
        var dy = Median(pairs.Select(p => p.B.Y - p.A.Y));
        var translation = Math.Sqrt(dx * dx + dy * dy);
        var residual = pairs.Max(p => Math.Sqrt(Math.Pow(p.B.X - p.A.X - dx, 2) + Math.Pow(p.B.Y - p.A.Y - dy, 2)));
        metrics["translationPixels"] = translation;
        metrics["translationResidualPixels"] = residual;
        if (translation > MaximumTranslationPixels || residual > MaximumTranslationResidualPixels)
            return Reject("G3_SEVERE_DEFOCUS_FIELD_UNSTABLE", "两帧星场漂移或同星相对位置变化过大，先保留跟踪/配准诊断，不自动调焦。");

        var fluxRatio = pairs.Max(p => Ratio(p.A.Flux, p.B.Flux));
        var radiusRatio = pairs.Max(p => Math.Max(Ratio(p.A.R50, p.B.R50), Ratio(p.A.R80, p.B.R80)));
        metrics["maximumFluxRatio"] = fluxRatio;
        metrics["maximumRadiusRatio"] = radiusRatio;
        if (fluxRatio > MaximumFluxRatio || radiusRatio > MaximumRadiusRatio)
            return Reject("G3_SEVERE_DEFOCUS_TRANSPARENCY_OR_SHAPE_CHANGED", "同星通量或半径变化过大；过云/短时形态变化尚不能与离焦区分。");

        var broad = pairs.Where(p => p.A.R50 >= SevereR50Pixels && p.B.R50 >= SevereR50Pixels &&
                                    p.A.R80 >= SevereR80Pixels && p.B.R80 >= SevereR80Pixels).Select(p => p.A.Id).ToArray();
        metrics["broadStars"] = broad.Length;
        metrics["broadFraction"] = broad.Length / (double)pairs.Length;
        metrics["firstMedianR50"] = Median(pairs.Select(p => p.A.R50));
        metrics["secondMedianR50"] = Median(pairs.Select(p => p.B.R50));
        metrics["firstMedianR80"] = Median(pairs.Select(p => p.A.R80));
        metrics["secondMedianR80"] = Median(pairs.Select(p => p.B.R80));
        if (broad.Length < MinimumStars || metrics["broadFraction"] < MinimumCommonFraction)
            return new(false, GateResult.Pass("G3_DEFOCUS_NOT_SEVERE",
                "未见至少三颗、占共同星群 75% 的星在两帧同时达到严重宽化门限；不自动调焦，也不新增阻断。", metrics), []);

        return new(true, GateResult.Warn("G3_SEVERE_DEFOCUS_CANDIDATE",
            "同一分散星群在两帧持续严重宽化；仅为离焦候选，不是目标身份、无云或最佳焦位证明。允许由原所有者在原预算/安全边界内尝试一次共享自动对焦，最终仍需扫焦和重复验证。", metrics), broad);
    }

    private static bool ValidFrame(SepFocusFrame frame) => string.IsNullOrWhiteSpace(frame.Error) &&
        frame.Reference is { Length: >= 3 and <= 30 } && frame.Stars is { Length: <= 30 } &&
        frame.Reference.All(r => r is not null && double.IsFinite(r.X) && double.IsFinite(r.Y) &&
            r.X is >= 0 and <= 32_000_000 && r.Y is >= 0 and <= 32_000_000) &&
        frame.Stars.All(s => s is not null) &&
        frame.Stars.Select(s => s.Id).Distinct().Count() == frame.Stars.Length &&
        frame.Stars.All(s => s.Id >= 0 && s.Id < frame.Reference.Length &&
            double.IsFinite(s.X) && double.IsFinite(s.Y) && s.X is >= 0 and <= 32_000_000 && s.Y is >= 0 and <= 32_000_000 &&
            double.IsFinite(s.R50) && double.IsFinite(s.R80) && s.R50 > 0 && s.R80 >= s.R50 &&
            double.IsFinite(s.Flux) && s.Flux > 0 && double.IsFinite(s.Snr) && s.Snr >= 15 &&
            (s.AxisRatio is null || double.IsFinite(s.AxisRatio.Value) && s.AxisRatio is > 0 and <= 1) &&
            (s.ApertureRadius is null || double.IsFinite(s.ApertureRadius.Value) && s.ApertureRadius > 0 && s.R80 < .85 * s.ApertureRadius));

    private static double Ratio(double a, double b) => Math.Min(double.MaxValue, Math.Max(a, b) / Math.Min(a, b));
    private static bool HasShape(SepFocusStar star) => star.AxisRatio is not null && star.ApertureRadius == 25;
    private static bool Eligible(SepFocusStar star) => HasShape(star) &&
        star.Snr >= MinimumSignalToNoise && star.AxisRatio >= MinimumAxisRatio;
    private static double Median(IEnumerable<double> values) => SepMainFocusRunner.Median(values);
    private static double Distance(SepFocusStar a, SepFocusStar b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
}
