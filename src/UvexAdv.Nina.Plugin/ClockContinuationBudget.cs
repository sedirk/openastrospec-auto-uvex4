using UvexAdv.Observatory;
using UvexAdv.Phd2;

namespace UvexAdv.Nina.Plugin;

// Numeric debit only. Coordinates, locks, epochs and frames cannot cross the
// clock repair. Old control envelopes remain immutable in their original archive.
internal sealed record ClockMotionDebit(int Attempts, double Distance, DateTimeOffset StartedUtc,
    int MaximumAttempts, double MaximumDistance, double MaximumSeconds)
{
    internal bool HasTime(DateTimeOffset now) => Attempts >= 0 && double.IsFinite(Distance) && Distance >= 0 &&
        Attempts < MaximumAttempts && Distance < MaximumDistance && now >= StartedUtc &&
        (now - StartedUtc).TotalSeconds < MaximumSeconds;
}

internal sealed record ClockContinuationBudget(ClockMotionDebit? G3, ClockMotionDebit? Phd2,
    double G3Single, double G3Radius, DateTimeOffset? FineStartedUtc, double FineMaximumSeconds,
    int CorrectionAttempts, double CorrectionDegrees, int ProbeAttempts, int MaximumProbeAttempts,
    int CoarseHandoffAttempts, int CoordinateSyncAttempts)
{
    internal bool HasRemainingBudget(DateTimeOffset now) =>
        (G3 is null || G3.HasTime(now)) && (Phd2 is null || Phd2.HasTime(now)) &&
        (FineStartedUtc is not { } start || now >= start && (now - start).TotalSeconds < FineMaximumSeconds) &&
        ProbeAttempts >= 0 && ProbeAttempts < MaximumProbeAttempts && CorrectionAttempts >= 0 &&
        double.IsFinite(CorrectionDegrees) && CorrectionDegrees >= 0;

    internal G3AcquisitionMotionState Charge(G3AcquisitionMotionState fresh) => G3 is not { } used ? fresh : fresh with
    {
        CorrectionAttempts = Math.Max(fresh.CorrectionAttempts, used.Attempts),
        CumulativeMotionArcseconds = Math.Max(fresh.CumulativeMotionArcseconds, used.Distance),
        StartedUtc = used.StartedUtc < fresh.StartedUtc ? used.StartedUtc : fresh.StartedUtc,
        MaximumCorrectionAttempts = Math.Min(fresh.MaximumCorrectionAttempts, used.MaximumAttempts),
        MaximumCumulativeMotionArcseconds = Math.Min(fresh.MaximumCumulativeMotionArcseconds, used.MaximumDistance),
        MaximumElapsedSeconds = Math.Min(fresh.MaximumElapsedSeconds, used.MaximumSeconds),
        MaximumSingleCorrectionArcseconds = Math.Min(fresh.MaximumSingleCorrectionArcseconds, G3Single),
        MaximumRadiusArcseconds = Math.Min(fresh.MaximumRadiusArcseconds, G3Radius),
    };

    internal Phd2LockShiftLedger Charge(Phd2LockShiftLedger fresh) => Phd2 is not { } used ? fresh : fresh with
    {
        AttemptsUsed = Math.Max(fresh.AttemptsUsed, used.Attempts),
        CumulativeCommandedPixels = Math.Max(fresh.CumulativeCommandedPixels, used.Distance),
        StartedUtc = used.StartedUtc < fresh.StartedUtc ? used.StartedUtc : fresh.StartedUtc,
    };
}
