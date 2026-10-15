using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

internal enum GuideRecoveryAction { WaitForStars, Reselect, ForceCalibration, Exhausted }

/// <summary>One logical target, including meridian segments. Never owns equipment or grants motion.</summary>
internal sealed class AdaptiveGuidingRecovery
{
    internal const string ExhaustedCode = "PHD2_ADAPTIVE_RECOVERY_EXHAUSTED_STOPPED";
    internal const string StarsTimeoutCode = "PHD2_STAR_WAIT_EXHAUSTED_STOPPED";
    internal const int MaximumSessionSeconds = 900;
    internal int Reselections { get; private set; }
    internal bool CalibrationConsumed { get; private set; }
    internal DateTimeOffset? StartedUtc { get; private set; }
    private int clearFrames;
    private DateTimeOffset? previousFrameUtc;
    private bool actionIssued;
    private bool inPlaceCheckConsumed;
    private readonly HashSet<(string Lineage, int Stage)> primaryStarSettles = [];
    internal bool TryConsumePrimaryStarSettle(string lineage = "legacy", int chargedStage = 1) =>
        !string.IsNullOrWhiteSpace(lineage) && chargedStage > 0 &&
        primaryStarSettles.Add((lineage, chargedStage));
    internal bool TryConsumeInPlaceCheck()
    {
        if (inPlaceCheckConsumed) return false;
        return inPlaceCheckConsumed = true;
    }

    internal static bool Handles(ObservationStage stage, string code) =>
        stage is ObservationStage.PlaceTargetOnSlit or ObservationStage.StartGuiding or
            ObservationStage.SelectAtrExposure or ObservationStage.RunScienceBlock &&
        code is "PHD2_STRUCTURED_GUIDE_SESSION_LOSS" or "PHD2_INITIAL_GUIDE_STAR_LOSS" or "GUIDING_LOST" or
            "GUIDING_UNSTABLE" or "GUIDING_NOT_STABLE" or
            Phd2DependencyRebuildStopPolicy.InitialSettleNotReachedCode or
            Phd2PlacementGuideWindowPolicy.LegacyPreMotionRejectedCode;

    internal void BeginCheck(DateTimeOffset now)
    {
        StartedUtc ??= now;
        clearFrames = 0;
        previousFrameUtc = null;
        actionIssued = false;
    }

    internal bool Expired(DateTimeOffset now) => StartedUtc is { } start &&
        (now < start || now - start >= TimeSpan.FromSeconds(MaximumSessionSeconds));

    internal static bool HasUsableStars(int stars, double snr, double saturatedFraction) =>
        stars >= 3 && double.IsFinite(snr) && snr >= 5 &&
        double.IsFinite(saturatedFraction) && saturatedFraction is >= 0 and < 0.01;

    internal GuideRecoveryAction Observe(DateTimeOffset now, DateTimeOffset frameUtc,
        bool validFrame, int stars, double snr, double saturatedFraction)
    {
        if (StartedUtc is null || actionIssued || Expired(now)) return GuideRecoveryAction.Exhausted;
        var fresh = validFrame && frameUtc <= now && now - frameUtc <= TimeSpan.FromSeconds(30) &&
            (previousFrameUtc is null || frameUtc > previousFrameUtc);
        // A stale/replayed frame breaks continuity; it cannot count twice.
        if (!fresh || !HasUsableStars(stars, snr, saturatedFraction)) clearFrames = 0;
        else clearFrames++;
        if (previousFrameUtc is null || frameUtc > previousFrameUtc) previousFrameUtc = frameUtc;
        if (clearFrames < 2) return GuideRecoveryAction.WaitForStars;
        actionIssued = true;
        if (Reselections == 0) { Reselections++; return GuideRecoveryAction.Reselect; }
        if (!CalibrationConsumed)
        {
            // Reserve before any async dispatch. Failure/cancel cannot reissue.
            CalibrationConsumed = true;
            return GuideRecoveryAction.ForceCalibration;
        }
        return GuideRecoveryAction.Exhausted;
    }

    internal void RecordExplicitCalibration() => CalibrationConsumed = true;
}
