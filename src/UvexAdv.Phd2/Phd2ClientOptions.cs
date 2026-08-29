using System.Net;

namespace UvexAdv.Phd2;

public sealed class Phd2ClientOptions
{
    public string Host { get; init; } = "127.0.0.1";

    public int Port { get; init; } = 4400;

    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan EventTimeoutMargin { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Minimum wall-clock allowance for each full-frame LoopingExposures event.
    /// Short exposures are still dominated by camera readout, USB transfer and
    /// PHD2 event dispatch; exposure duration plus a small generic margin is not
    /// a sufficient timeout for those frames.
    /// </summary>
    // The commissioned G3 normally publishes a full-frame event in roughly
    // 1-5 seconds even for a 10/20 ms exposure, but the PHD2/camera pipeline
    // has demonstrated occasional 20+ second USB/readout/event-dispatch stalls.
    // The timeout covers the complete native command -> sensor readout -> FITS
    // flush -> SingleFrameComplete event path, not merely the exposure itself.
    // Keep a finite bound while allowing one such transient to finish normally.
    public TimeSpan MinimumLoopingFrameEventTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public TimeSpan FileReadyTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public double GuideStarSelectionTolerancePixels { get; init; } = 64;

    public TimeSpan CalibrationValidationTtl { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan StopConfirmationTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan StatePollInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    public bool AllowNonLoopbackEndpoint { get; init; }

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Host))
        {
            throw new ArgumentException("PHD2 host is required.", nameof(Host));
        }

        if (Port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(Port), "PHD2 port must be between 1 and 65535.");
        }

        ValidatePositiveTimeout(CommandTimeout, nameof(CommandTimeout));
        ValidatePositiveTimeout(EventTimeoutMargin, nameof(EventTimeoutMargin));
        ValidatePositiveTimeout(MinimumLoopingFrameEventTimeout, nameof(MinimumLoopingFrameEventTimeout));
        ValidatePositiveTimeout(FileReadyTimeout, nameof(FileReadyTimeout));
        ValidatePositiveTimeout(CalibrationValidationTtl, nameof(CalibrationValidationTtl));
        ValidatePositiveTimeout(StopConfirmationTimeout, nameof(StopConfirmationTimeout));
        ValidatePositiveTimeout(StatePollInterval, nameof(StatePollInterval));
        if (StatePollInterval > StopConfirmationTimeout)
        {
            throw new ArgumentOutOfRangeException(
                nameof(StatePollInterval),
                "State polling interval cannot exceed the stop-confirmation timeout.");
        }

        if (!double.IsFinite(GuideStarSelectionTolerancePixels) || GuideStarSelectionTolerancePixels <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(GuideStarSelectionTolerancePixels),
                "Guide-star selection tolerance must be finite and greater than zero.");
        }

        if (!AllowNonLoopbackEndpoint && !IsLoopbackHost(Host))
        {
            throw new ArgumentException(
                "PHD2 is restricted to a loopback endpoint unless AllowNonLoopbackEndpoint is explicitly enabled.",
                nameof(Host));
        }
    }

    private static bool IsLoopbackHost(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
    }

    private static void ValidatePositiveTimeout(TimeSpan value, string parameterName)
    {
        if (value <= TimeSpan.Zero || value == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Timeout must be finite and greater than zero.");
        }
    }
}
