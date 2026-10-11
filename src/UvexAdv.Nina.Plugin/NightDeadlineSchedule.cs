using System.Globalization;
using System.Text.RegularExpressions;

namespace UvexAdv.Nina.Plugin;

/// <summary>Resolve a saved wall clock once, never a rolling 'next occurrence'.</summary>
internal sealed record NightDeadlineSchedule(string RequestedText, DateTimeOffset ResolvedAtUtc,
    string TimeZoneId, string? ObservingNightDate, DateTimeOffset? DeadlineUtc)
{
    internal const string InvalidText = "截止时间须为 HH:mm（例如 05:00，可带秒）；旧绝对截止时间必须包含时区。";

    internal static bool IsValid(string? text) => string.IsNullOrWhiteSpace(text) ||
        TryClock(text.Trim(), out _) || TryAbsolute(text.Trim(), out _);

    internal static NightDeadlineSchedule Resolve(string? text, DateTimeOffset now, TimeZoneInfo zone)
    {
        var requested = text?.Trim() ?? string.Empty;
        if (requested.Length == 0) return new(requested, now.ToUniversalTime(), zone.Id, null, null);
        if (TryClock(requested, out var clock))
        {
            // Noon separates observing nights. An elapsed morning deadline stays
            // elapsed, including after dawn; retries must not acquire another day.
            var localNow = TimeZoneInfo.ConvertTime(now, zone).DateTime;
            var nightDate = localNow.Date.AddDays(localNow.Hour < 12 ? -1 : 0);
            var localEnd = nightDate.AddDays(clock.Hour < 12 ? 1 : 0).Add(clock.ToTimeSpan());
            if (zone.IsInvalidTime(localEnd) || zone.IsAmbiguousTime(localEnd))
                throw new InvalidOperationException("截止时刻遇到夏令时跳时或重时；请显式填写带时区的绝对截止时间。");
            var end = new DateTimeOffset(localEnd, zone.GetUtcOffset(localEnd)).ToUniversalTime();
            return new(requested, now.ToUniversalTime(), zone.Id,
                nightDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), end);
        }
        if (TryAbsolute(requested, out var absolute))
            return new(requested, now.ToUniversalTime(), zone.Id, null, absolute.ToUniversalTime());
        throw new InvalidOperationException(InvalidText);
    }

    private static bool TryClock(string text, out TimeOnly clock) =>
        TimeOnly.TryParseExact(text, ["HH:mm", "HH:mm:ss"], CultureInfo.InvariantCulture,
            DateTimeStyles.None, out clock);

    private static bool TryAbsolute(string text, out DateTimeOffset value)
    {
        value = default;
        return Regex.IsMatch(text, @"^\d{4}-\d{2}-\d{2}T.+(Z|[+-]\d{2}:\d{2})$") &&
            DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
    }
}
