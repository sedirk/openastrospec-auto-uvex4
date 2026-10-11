using UvexAdv.Nina.Plugin;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class NightDeadlineScheduleTests
{
    private static readonly TimeZoneInfo Site = TimeZoneInfo.CreateCustomTimeZone("test +08", TimeSpan.FromHours(8), "Site", "Site");

    [Theory]
    [InlineData("2026-10-10T19:00:00+08:00", "00:30", "2026-10-11T00:30:00+08:00")]
    [InlineData("2026-10-11T00:30:00+08:00", "05:00", "2026-10-11T05:00:00+08:00")]
    [InlineData("2026-10-11T02:00:00+08:00", "05:00", "2026-10-11T05:00:00+08:00")]
    [InlineData("2026-10-11T05:01:00+08:00", "05:00", "2026-10-11T05:00:00+08:00")]
    [InlineData("2026-10-11T08:00:00+08:00", "05:00", "2026-10-11T05:00:00+08:00")]
    [InlineData("2026-10-10T23:45:00+08:00", "23:30", "2026-10-10T23:30:00+08:00")]
    [InlineData("2026-10-11T02:00:00+08:00", "23:30", "2026-10-10T23:30:00+08:00")]
    [InlineData("2026-10-10T19:00:00+08:00", "00:00", "2026-10-11T00:00:00+08:00")]
    [InlineData("2026-10-10T19:00:00+08:00", "05:00:30", "2026-10-11T05:00:30+08:00")]
    [InlineData("2026-12-31T19:00:00+08:00", "05:00", "2027-01-01T05:00:00+08:00")]
    [InlineData("2028-02-28T19:00:00+08:00", "05:00", "2028-02-29T05:00:00+08:00")]
    [InlineData("2026-10-11T11:59:59+08:00", "05:00", "2026-10-11T05:00:00+08:00")]
    [InlineData("2026-10-11T12:00:00+08:00", "05:00", "2026-10-12T05:00:00+08:00")]
    [InlineData("2026-10-11T12:00:00+08:00", "12:00", "2026-10-11T12:00:00+08:00")]
    public void ClockResolvesWithinTheNoonAnchoredNightNotTheNextOccurrence(string start, string clock, string expected)
    {
        var schedule = NightDeadlineSchedule.Resolve(clock, DateTimeOffset.Parse(start), Site);
        Assert.Equal(DateTimeOffset.Parse(expected), schedule.DeadlineUtc);
        Assert.Equal(TimeSpan.Zero, schedule.DeadlineUtc!.Value.Offset);
        Assert.Equal(clock, schedule.RequestedText);
        Assert.Equal(Site.Id, schedule.TimeZoneId);
        Assert.NotNull(schedule.ObservingNightDate);
    }

    [Fact]
    public void ExplicitNewNightReusesClockButActivePlanNeverRecomputesAfterMidnightNoonRecoveryOrZoneChange()
    {
        var first = NightDeadlineSchedule.Resolve("05:00", DateTimeOffset.Parse("2026-10-10T19:00:00+08:00"), Site);
        var next = NightDeadlineSchedule.Resolve("05:00", DateTimeOffset.Parse("2026-10-11T19:00:00+08:00"), Site);
        Assert.Equal(TimeSpan.FromDays(1), next.DeadlineUtc - first.DeadlineUtc);
        var plan = new NativeSequencePlan(1, 1, 120, DeadlineUtc: first.DeadlineUtc);
        foreach (var now in new[] { "2026-10-11T05:00:00+08:00", "2026-10-11T12:01:00+08:00", "2026-10-12T04:00:00+08:00" })
            Assert.Equal("NIGHT_DEADLINE_REACHED", plan.StopGate(DateTimeOffset.Parse(now), _ => -30)!.Code);
        Assert.Equal("2026-10-10", first.ObservingNightDate);
    }

    [Theory]
    [InlineData("24:00")]
    [InlineData("05:60")]
    [InlineData("5:00")]
    [InlineData("05:00:60")]
    [InlineData("05:00+08:00")]
    [InlineData("05:00Z")]
    [InlineData("2026-10-11T05:00:00")]
    [InlineData("tomorrow")]
    public void InvalidOrAmbiguousInputDoesNotRemoveOrInventDeadline(string text)
    {
        Assert.False(NightDeadlineSchedule.IsValid(text));
        Assert.Throws<InvalidOperationException>(() => NightDeadlineSchedule.Resolve(text, DateTimeOffset.UtcNow, Site));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void OptionalBlankKeepsLegacyNoDeadlineSemantics(string? text)
    {
        Assert.True(NightDeadlineSchedule.IsValid(text));
        Assert.Null(NightDeadlineSchedule.Resolve(text, DateTimeOffset.UtcNow, Site).DeadlineUtc);
    }

    [Theory]
    [InlineData("2026-10-11T05:00:00+08:00")]
    [InlineData("2026-10-10T21:00:00Z")]
    [InlineData("2026-10-10T16:00:00-05:00")]
    public void LegacyAbsoluteDeadlineDoesNotMigrateSilently(string text)
    {
        Assert.True(NightDeadlineSchedule.IsValid(text));
        var resolved = NightDeadlineSchedule.Resolve(text, DateTimeOffset.Parse("2027-01-01T00:00:00Z"), Site);
        Assert.Equal(DateTimeOffset.Parse(text), resolved.DeadlineUtc);
        Assert.Null(resolved.ObservingNightDate);
    }

    [Theory]
    [InlineData("2026-03-07T19:00:00-05:00", "02:30")]
    [InlineData("2026-11-01T00:30:00-04:00", "01:30")]
    public void DstSkippedOrRepeatedDeadlineNeedsAnExplicitOffset(string now, string clock)
    {
        var eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        Assert.Throws<InvalidOperationException>(() => NightDeadlineSchedule.Resolve(clock, DateTimeOffset.Parse(now), eastern));
        Assert.NotNull(NightDeadlineSchedule.Resolve("2026-11-01T01:30:00-04:00", DateTimeOffset.Parse(now), eastern).DeadlineUtc);
    }
}
