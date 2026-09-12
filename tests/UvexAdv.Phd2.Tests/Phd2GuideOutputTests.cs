using UvexAdv.Phd2;

namespace UvexAdv.Phd2.Tests;

public sealed class Phd2GuideOutputTests
{
    private static readonly DateTimeOffset Epoch = DateTimeOffset.Parse("2026-09-11T15:14:52Z");
    private static Phd2GuideStep Step(long frame, double ra = -1.786, double dec = 2.326,
        int? raDuration = null, int? decDuration = null) =>
        new(frame, -1.548, -1.798, 143.67, 5.36, 1.39, 1, "Mount", ra, dec, raDuration, decDuration);

    [Fact]
    public void ActualTrn29NoPulseSeriesFailsAtThirdFreshFrameNotAtBudgetExhaustion()
    {
        Phd2GuideOutputStatus? status = null;
        status = Phd2GuideOutputStatus.Observe(status, Step(4), Epoch);
        Assert.False(status!.Failed);
        status = Phd2GuideOutputStatus.Observe(status, Step(5, -8.385, 8.867), Epoch.AddSeconds(2.24));
        Assert.False(status!.Failed);
        status = Phd2GuideOutputStatus.Observe(status, Step(6, -9.571, 9.681), Epoch.AddSeconds(4.48));
        Assert.True(status!.Failed);
        Assert.Equal(3, status.ConsecutiveMissingOutputs);
    }

    [Theory]
    [InlineData(0, 0, null, null)]
    [InlineData(0.001, 0.001, null, null)]
    [InlineData(30, 20, 100, null)]
    [InlineData(30, 20, null, 100)]
    public void NoCorrectionDeadbandAndRealPulseRemainAllowed(double ra, double dec, int? raMs, int? decMs)
    {
        Phd2GuideOutputStatus? status = null;
        for (var i = 0; i < 10; i++) status = Phd2GuideOutputStatus.Observe(status, Step(i, ra, dec, raMs, decMs), Epoch.AddSeconds(i * 2));
        Assert.False(status!.Failed);
    }

    [Theory]
    [InlineData("AO", 1, true)]
    [InlineData(null, 1, true)]
    [InlineData("Mount", 2, true)]
    [InlineData("Mount", 1, false)]
    public void MissingTelemetryOrLostStarDoesNotProveOutputFailure(string? mount, int error, bool fields)
    {
        Phd2GuideOutputStatus? status = null;
        for (var i = 0; i < 10; i++) status = Phd2GuideOutputStatus.Observe(status,
            Step(i) with { Mount = mount, ErrorCode = error, RaGuideDistancePixels = fields ? 2 : null }, Epoch.AddSeconds(i * 2));
        Assert.Null(status);
    }

    [Fact]
    public void DuplicateOrLateFramesDoNotAccumulateAndShortBurstsDoNotFail()
    {
        var first = Phd2GuideOutputStatus.Observe(null, Step(5), Epoch);
        Assert.Equal(first, Phd2GuideOutputStatus.Observe(first, Step(5), Epoch.AddSeconds(10)));
        Assert.Equal(first, Phd2GuideOutputStatus.Observe(first, Step(4), Epoch.AddSeconds(10)));
        var fast = Phd2GuideOutputStatus.Observe(first, Step(6), Epoch.AddMilliseconds(10));
        fast = Phd2GuideOutputStatus.Observe(fast, Step(7), Epoch.AddMilliseconds(20));
        Assert.False(fast!.Failed);
        var recovered = Phd2GuideOutputStatus.Observe(fast, Step(8, raDuration: 20), Epoch.AddSeconds(1));
        Assert.Equal(0, recovered!.ConsecutiveMissingOutputs);
    }

    [Fact]
    public void FaultLatchesAndCannotGrantSuccessfulSettle()
    {
        Phd2GuideOutputStatus? status = null;
        for (var i = 0; i < 3; i++) status = Phd2GuideOutputStatus.Observe(status, Step(i), Epoch.AddSeconds(i * 2));
        Assert.True(status!.Failed);
        Assert.Equal(status, Phd2GuideOutputStatus.Observe(status, Step(4, raDuration: 100), Epoch.AddSeconds(8)));
        var state = Phd2StateSnapshot.Disconnected with
        {
            IsConnected = true, AppState = Phd2AppState.Guiding,
            LastSettle = new(true, null, 3, 0, Epoch), LastSettleOperationId = 1,
            LastSettleCommandAccepted = true, LastSettleConnectionEpoch = 0, LastSettleGuideEpoch = 0,
        };
        Assert.True(state.HasCurrentSuccessfulSettle);
        Assert.False((state with { GuideOutput = status }).HasCurrentSuccessfulSettle);
    }
}
