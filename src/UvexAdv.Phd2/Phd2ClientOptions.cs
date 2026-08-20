using System.Net;

namespace UvexAdv.Phd2;

public sealed class Phd2ClientOptions
{
    public string Host { get; init; } = "127.0.0.1";

    public int Port { get; init; } = 4400;

    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan EventTimeoutMargin { get; init; } = TimeSpan.FromSeconds(5);

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
