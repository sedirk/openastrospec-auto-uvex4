using NINA.Astrometry;
using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class NightDawnBoundaryTests
{
    public NightDawnBoundaryTests() => ReadOnlyNinaAstrometry.Initialize();

    private static readonly DateTimeOffset Evening = DateTimeOffset.Parse("2026-10-21T18:00:31+08:00");
    private static readonly NativeSequencePlan Plan = new(2, 4, 0, StopAtDawn: true);

    internal static double EveningLongitude(DateTimeOffset now) => Enumerable.Range(-180, 360).First(lon =>
    {
        var site = new ObserverInfo { Latitude = 0, Longitude = lon };
        var current = AstroUtil.GetSunAltitude(now.UtcDateTime, site);
        return current is > -10 and < -8 && current < AstroUtil.GetSunAltitude(now.AddMinutes(-1).UtcDateTime, site);
    });

    [Theory]
    [InlineData("skip")]
    [InlineData("cleanup-failure")]
    [InlineData("end-policy")]
    [InlineData("late-unsafe")]
    [InlineData("late-cancel")]
    [InlineData("late-deadline")]
    [InlineData("late-dawn")]
    public async Task EveningActuallyExecutesNativeTargetsButKeepsAllTerminalOverrides(string boundary)
    {
        await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync(boundary,
            "G3_POST_WCS_SHORT_UNCONFIRMED", eveningTwilight: true);
    }

    [Theory]
    [InlineData(-10, -10.2, false)] // Evening, already below the horizon.
    [InlineData(-11.8, -12, false)] // Descending through the saved threshold.
    [InlineData(-12.2, -12, true)] // Rising through the same threshold.
    [InlineData(-10.2, -10, true)] // Starting for the first time after dawn.
    [InlineData(-14, -13, false)]
    [InlineData(-13, -14, false)]
    [InlineData(40.2, 40, true)] // Afternoon daylight is not evening twilight.
    [InlineData(0.2, 0, true)]
    [InlineData(-12, -12, true)] // No descending evidence: retain the stop.
    public void DawnUsesSolarDirectionWithoutDisablingDaylight(double previous, double current, bool stop)
    {
        var reads = new List<DateTimeOffset>();
        var gate = Plan.StopGate(Evening, time =>
        {
            reads.Add(time);
            return time == Evening ? current : previous;
        });
        Assert.Equal(stop ? "NIGHT_DAWN_REACHED" : null, gate?.Code);
        Assert.Equal(Evening, reads[0]);
        if (current >= -12) Assert.Equal(Evening.AddMinutes(-1), reads[1]);
        if (gate is not null)
        {
            Assert.False(NativeSequencePlan.IsSkippableQualityFailure(gate));
            Assert.Equal(current, gate.Metrics!["sunAltitudeDegrees"]);
            Assert.Equal(previous, gate.Metrics["sunAltitudeOneMinuteEarlierDegrees"]);
        }
    }

    [Theory]
    [InlineData(-18)]
    [InlineData(-12)]
    [InlineData(-6)]
    public void SavedThresholdIsUnchangedInBothDirections(double threshold)
    {
        var plan = Plan with { DawnSunAltitudeDegrees = threshold };
        Assert.Null(plan.StopGate(Evening, t => t == Evening ? threshold : threshold + 0.2));
        Assert.Equal("NIGHT_DAWN_REACHED", plan.StopGate(Evening,
            t => t == Evening ? threshold : threshold - 0.2)!.Code);
    }

    [Theory]
    [InlineData(double.NaN, -10)]
    [InlineData(double.PositiveInfinity, -10)]
    [InlineData(-10, double.NaN)]
    public void UnavailableSolarEvidenceDoesNotAuthorizeContinuation(double current, double previous)
    {
        var gate = Plan.StopGate(Evening, t => t == Evening ? current : previous);
        Assert.Equal("NIGHT_SAFETY_READ_FAILED", gate!.Code);
        Assert.False(NativeSequencePlan.IsSkippableQualityFailure(gate));
    }

    [Fact]
    public void DeadlineKeepsPriorityAndDisabledDawnDoesNotReadTheSun()
    {
        double NoRead(DateTimeOffset _) => throw new InvalidOperationException("No ephemeris read expected.");
        Assert.Equal("NIGHT_DEADLINE_REACHED", (Plan with { DeadlineUtc = Evening }).StopGate(Evening, NoRead)!.Code);
        Assert.Null((Plan with { StopAtDawn = false }).StopGate(Evening, NoRead));
        var target = (Plan with { TargetDeadlineUtc = Evening }).StopGate(Evening, t => t == Evening ? -10 : -9.8);
        Assert.Equal("TARGET_CONTINUATION_TIME_LIMIT", target!.Code);
    }

    [Fact]
    public void October21EveningIsNotDawnButFollowingMorningStillStops()
    {
        // Rounded representative eastern-China site, not a private device Profile.
        double Sun(DateTimeOffset t) => AstroUtil.GetSunAltitude(t.UtcDateTime,
            new ObserverInfo { Latitude = 33.4, Longitude = 120.4 });
        var jd = AstroUtil.GetJulianDate(Evening.UtcDateTime);
        var sun = AstroUtil.GetSunPosition(Evening.UtcDateTime, jd, new ObserverInfo { Latitude = 33.4, Longitude = 120.4 });
        Assert.True(double.IsFinite(Sun(Evening)), $"JD={jd}; DeltaT={AstroUtil.DeltaT(Evening.UtcDateTime)}; LST={AstroUtil.GetLocalSiderealTime(Evening.UtcDateTime, 120.4)}; sun RA={sun.RA}, Dec={sun.Dec}");
        Assert.InRange(Sun(Evening), -12, 0);
        Assert.True(Sun(Evening) < Sun(Evening.AddMinutes(-1)));
        Assert.Null(Plan.StopGate(Evening, Sun));
        Assert.Null(Plan.StopGate(Evening.ToUniversalTime(), Sun));
        var morning = DateTimeOffset.Parse("2026-10-22T05:30:00+08:00");
        Assert.InRange(Sun(morning), -12, 0);
        Assert.True(Sun(morning) > Sun(morning.AddMinutes(-1)));
        Assert.Equal("NIGHT_DAWN_REACHED", Plan.StopGate(morning, Sun)!.Code);
        Assert.Equal("NIGHT_DAWN_REACHED", Plan.StopGate(
            DateTimeOffset.Parse("2026-10-22T14:00:00+08:00"), Sun)!.Code);
    }
}
