namespace UvexAdv.Nina.Plugin;

// ADR-0031: scientific consent cannot turn weather/roof protection on or off.
// The legacy field name is retained only for saved Profile compatibility.
internal sealed record ScienceQualityPolicy(bool WarningProbeEnabled, bool LegacyDegradedConsent)
{
    public bool AllowsMeasuredContinuation => WarningProbeEnabled || LegacyDegradedConsent;
    public string Name => WarningProbeEnabled ? "MeasuredWarningProbe" : "StrictSlitAcceptance";

    public static ScienceQualityPolicy From(RealRunConfiguration configuration) => new(
        configuration.AllowSupervisedSlitQualityWarning,
        configuration.AllowDegradedSupervisedScience || configuration.Environment.WeakSupervisionEnabled);

    // Shared by standalone and native sequence execution, and recorded-run
    // regression. Geometry confirmation includes current owner/epoch/identity;
    // a warning never supplies any of those missing facts.
    public SlitWarningProbeDecision EvaluateSlitWarning(
        bool measuredGeometryConfirmed, bool canWaitWithoutNewMotion,
        IReadOnlyList<double> residuals, IReadOnlyList<Phd2SlitApertureResidual?> apertures,
        double tolerance, double nearAllowance, double acquisitionEnvelope)
    {
        if (residuals.Count < 3 || residuals.Any(value => !double.IsFinite(value) || value < 0))
            return new(false, false);
        var along = Phd2PlacementGuideWindowPolicy.CanProbeAlongSlitWithPrecisionWarning(
            WarningProbeEnabled, measuredGeometryConfirmed && apertures.Count == residuals.Count, apertures,
            tolerance, nearAllowance, acquisitionEnvelope);
        var warning = canWaitWithoutNewMotion &&
            !Phd2PlacementGuideWindowPolicy.AllWithinTolerance(residuals, tolerance) &&
            (along || Phd2PlacementGuideWindowPolicy.CanProbeWithPrecisionWarning(
                WarningProbeEnabled, measuredGeometryConfirmed, residuals,
                tolerance, nearAllowance, acquisitionEnvelope));
        return new(warning, along);
    }

    public bool AllowsPreExposureWarning(bool sameEpoch, bool measuredIdentityConfirmed,
        IReadOnlyList<double> residuals, double acquisitionEnvelope) =>
        WarningProbeEnabled && sameEpoch && measuredIdentityConfirmed && residuals.Count >= 3 &&
        Phd2PlacementGuideWindowPolicy.AllWithinTolerance(residuals, acquisitionEnvelope);

    // A large clipped core can overlap the slit while its measured centre is
    // consistently outside it. Keep warning probes, but prefer ONE already
    // feasible normal correction before handing that window to spectroscopy.
    // Along-slit error and alternating seeing samples do not request a move.
    internal static bool HasConsistentCrossSlitBias(IReadOnlyList<Phd2SlitApertureResidual?> apertures,
        double tolerance) => double.IsFinite(tolerance) && tolerance > 0 && apertures.Count >= 3 &&
        apertures.All(a => a is not null && double.IsFinite(a.CrossSlitPixels) &&
            double.IsFinite(a.HalfWidthPixels) && a.HalfWidthPixels > 0 &&
            double.IsFinite(a.UncertaintyPixels) && a.UncertaintyPixels >= 0 &&
            Math.Abs(a.CrossSlitPixels) > Math.Max(tolerance, a.HalfWidthPixels + a.UncertaintyPixels) &&
            Math.Sign(a.CrossSlitPixels) == Math.Sign(apertures[0]!.CrossSlitPixels));

    public static string StatusText(bool warningProbe, bool weakEnvironment, bool english) =>
        (warningProbe, weakEnvironment, english) switch
        {
            (true, false, false) => "入缝策略：警告后试拍已启用 · 完整安全链不变",
            (true, true, false) => "入缝策略：警告后试拍已启用 · 有人弱监督",
            (false, _, false) => "入缝策略：严格验收 · 未启用警告后试拍",
            (true, false, true) => "Slit policy: warning probes enabled · full safety unchanged",
            (true, true, true) => "Slit policy: warning probes enabled · supervised environment",
            _ => "Slit policy: strict acceptance · warning probes disabled",
        };
}

internal sealed record SlitWarningProbeDecision(bool CanProbe, bool AlongSlitWarning);
