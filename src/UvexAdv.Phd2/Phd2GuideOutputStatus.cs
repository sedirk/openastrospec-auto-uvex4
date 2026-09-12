namespace UvexAdv.Phd2;

/// <summary>
/// Native GuideStep output evidence, not a guide-precision or star-SNR gate.
/// PHD2 omits duration fields when they are zero. ErrorCode describes the
/// star (1 = saturated), NOT whether the mount accepted a pulse.
/// </summary>
public sealed record Phd2GuideOutputStatus(
    long LastFrame,
    int ConsecutiveMissingOutputs,
    DateTimeOffset FirstMissingUtc,
    DateTimeOffset UpdatedUtc,
    bool Failed,
    Phd2GuideStep LastStep)
{
    public const string FailureCode = "PHD2_GUIDE_OUTPUT_UNAVAILABLE";
    public const int RequiredFrames = 3;
    public static readonly TimeSpan MinimumWindow = TimeSpan.FromSeconds(2);

    public static Phd2GuideOutputStatus? Observe(
        Phd2GuideOutputStatus? previous, Phd2GuideStep step, DateTimeOffset now)
    {
        // A latched device-output fault cannot be cleared by a lock change,
        // a late good frame, Stop/Guide, or a TCP reconnect. Rebinding the
        // equipment explicitly is the only client-side reset.
        if (previous?.Failed == true) return previous;
        if (step.Frame is not { } frame || frame < 0) return null;
        if (previous is not null && (frame <= previous.LastFrame || now <= previous.UpdatedUtc))
            return previous;
        if (step.Mount != "Mount" || step.ErrorCode is not (null or 0 or 1) ||
            step.RaGuideDistancePixels is not { } ra || !double.IsFinite(ra) ||
            step.DecGuideDistancePixels is not { } dec || !double.IsFinite(dec) ||
            step.RaDurationMilliseconds is < 0 || step.DecDurationMilliseconds is < 0)
            return null; // Missing/legacy/AO telemetry is not evidence of zero output.

        // Ignore tiny requests that can legitimately round below one native
        // millisecond. This is an output-diagnostic floor, not motion authority.
        var missing = Math.Max(Math.Abs(ra), Math.Abs(dec)) >= 0.1 &&
            (step.RaDurationMilliseconds ?? 0) == 0 && (step.DecDurationMilliseconds ?? 0) == 0;
        var consecutive = missing ? (previous?.ConsecutiveMissingOutputs ?? 0) + 1 : 0;
        var first = consecutive > 1 ? previous!.FirstMissingUtc : now;
        return new(frame, consecutive, first, now,
            consecutive >= RequiredFrames && now - first >= MinimumWindow, step);
    }
}

public sealed class Phd2GuideOutputException(Phd2GuideOutputStatus status)
    : Phd2Exception($"{Phd2GuideOutputStatus.FailureCode}: PHD2 requested nonzero mount corrections in " +
        $"{status.ConsecutiveMissingOutputs} consecutive frames but reported no RA or DEC pulse output. " +
        "Connected/Guiding and star centroids do not prove a working mount connection.")
{
    public Phd2GuideOutputStatus Status { get; } = status;
}
