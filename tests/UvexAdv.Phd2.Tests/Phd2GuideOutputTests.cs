using UvexAdv.Phd2;

namespace UvexAdv.Phd2.Tests;

public sealed class Phd2GuideOutputTests
{
    private static readonly DateTimeOffset Epoch = DateTimeOffset.Parse("2026-09-11T15:14:52Z");
    private const string MountName = "On-Step (ASCOM)";
    private static readonly Phd2Equipment Equipment = new(null, new(MountName, true), null, null, null);
    private static Phd2GuideOutputStatus? Observe(Phd2GuideOutputStatus? previous,
        Phd2GuideStep step, DateTimeOffset now) => Phd2GuideOutputStatus.Observe(previous, step, now, Equipment);
    private static Phd2GuideStep Step(long frame, double ra = -1.786, double dec = 2.326,
        int? raDuration = null, int? decDuration = null) =>
        new(frame, -1.548, -1.798, 143.67, 5.36, 1.39, 1, MountName, ra, dec, raDuration, decDuration);

    [Fact]
    public void ActualTrn29NoPulseSeriesFailsAtThirdFreshFrameNotAtBudgetExhaustion()
    {
        Phd2GuideOutputStatus? status = null;
        status = Observe(status, Step(4), Epoch);
        Assert.False(status!.Failed);
        status = Observe(status, Step(5, -8.385, 8.867), Epoch.AddSeconds(2.24));
        Assert.False(status!.Failed);
        status = Observe(status, Step(6, -9.571, 9.681), Epoch.AddSeconds(4.48));
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
        for (var i = 0; i < 10; i++) status = Observe(status, Step(i, ra, dec, raMs, decMs), Epoch.AddSeconds(i * 2));
        Assert.False(status!.Failed);
    }

    [Theory]
    [InlineData("AO", 1, true)]
    [InlineData(null, 1, true)]
    [InlineData(MountName, 2, true)]
    [InlineData(MountName, 1, false)]
    public void MissingTelemetryOrLostStarDoesNotProveOutputFailure(string? mount, int error, bool fields)
    {
        Phd2GuideOutputStatus? status = null;
        for (var i = 0; i < 10; i++) status = Observe(status,
            Step(i) with { Mount = mount, ErrorCode = error, RaGuideDistancePixels = fields ? 2 : null }, Epoch.AddSeconds(i * 2));
        Assert.Null(status);
    }

    [Fact]
    public void DuplicateOrLateFramesDoNotAccumulateAndShortBurstsDoNotFail()
    {
        var first = Observe(null, Step(5), Epoch);
        Assert.Equal(first, Observe(first, Step(5), Epoch.AddSeconds(10)));
        Assert.Equal(first, Observe(first, Step(4), Epoch.AddSeconds(10)));
        var fast = Observe(first, Step(6), Epoch.AddMilliseconds(10));
        fast = Observe(fast, Step(7), Epoch.AddMilliseconds(20));
        Assert.False(fast!.Failed);
        var recovered = Observe(fast, Step(8, raDuration: 20), Epoch.AddSeconds(1));
        Assert.Equal(0, recovered!.ConsecutiveMissingOutputs);
    }

    [Fact]
    public void FaultLatchesAndCannotGrantSuccessfulSettle()
    {
        Phd2GuideOutputStatus? status = null;
        for (var i = 0; i < 3; i++) status = Observe(status, Step(i), Epoch.AddSeconds(i * 2));
        Assert.True(status!.Failed);
        Assert.Equal(status, Observe(status, Step(4, raDuration: 100), Epoch.AddSeconds(8)));
        var state = Phd2StateSnapshot.Disconnected with
        {
            IsConnected = true, AppState = Phd2AppState.Guiding,
            LastSettle = new(true, null, 3, 0, Epoch), LastSettleOperationId = 1,
            LastSettleCommandAccepted = true, LastSettleConnectionEpoch = 0, LastSettleGuideEpoch = 0,
        };
        Assert.True(state.HasCurrentSuccessfulSettle);
        Assert.False((state with { GuideOutput = status }).HasCurrentSuccessfulSettle);
    }

    [Fact]
    public void V460CygNativeDriverNameAndOmittedZeroDurationsFailBeforeCoarseRebuild()
    {
        // 2026-10-06 22:17:45--50: actual frame 3--5 requested corrections.
        // Native debug log reports PulseGuide RPC 0x800706ba; ErrorCode=1 is saturation.
        var status = Observe(null, Step(3, -2.213, -1.697), Epoch);
        status = Observe(status, Step(4, -2.256, -1.710), Epoch.AddSeconds(2.223));
        Assert.False(status!.Failed);
        status = Observe(status, Step(5, -3.765, -2.335), Epoch.AddSeconds(4.452));
        Assert.True(status!.Failed);
        Assert.Equal(MountName, status.LastStep.Mount);
        Assert.Equal(1, status.LastStep.ErrorCode);
        Assert.Null(status.LastStep.RaDurationMilliseconds);
    }

    [Theory]
    [InlineData("Mount")]
    [InlineData("mount")]
    [InlineData("On-Step (ASCOM) ")]
    [InlineData("on-step (ASCOM)")]
    [InlineData("AO device")]
    [InlineData("Other mount")]
    public void UnmatchedNativeSourcesNeverBecomeMountEvidence(string source)
    {
        Phd2GuideOutputStatus? status = null;
        for (var i = 0; i < 4; i++)
            status = Observe(status, Step(i) with { Mount = source }, Epoch.AddSeconds(i * 2));
        Assert.Null(status);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("missing-mount")]
    [InlineData("disconnected")]
    [InlineData("blank-name")]
    [InlineData("ambiguous-ao")]
    public void MissingOrAmbiguousEquipmentCannotProveZeroOutput(string scenario)
    {
        var equipment = scenario switch
        {
            "unknown" => null,
            "missing-mount" => Equipment with { Mount = null },
            "disconnected" => Equipment with { Mount = new(MountName, false) },
            "blank-name" => Equipment with { Mount = new(" ", true) },
            _ => Equipment with { AdaptiveOptics = new(MountName, true) },
        };
        var prior = Observe(null, Step(1), Epoch);
        Assert.Null(Phd2GuideOutputStatus.Observe(prior, Step(2), Epoch.AddSeconds(2), equipment));
    }

    [Fact]
    public void InterleavedAoEventsDoNotAdvanceOrEraseCurrentMountSeries()
    {
        var first = Observe(null, Step(1), Epoch);
        var ao = Step(100, raDuration: 5) with { Mount = "AO device" };
        Assert.Equal(first, Observe(first, ao, Epoch.AddSeconds(1)));
        var second = Observe(first, Step(2), Epoch.AddSeconds(2));
        Assert.Equal(second, Observe(second, ao, Epoch.AddSeconds(3)));
        Assert.True(Observe(second, Step(3), Epoch.AddSeconds(4))!.Failed);
    }

    [Fact]
    public void DifferentBoundMountStartsNewSeriesWithoutAdoptingOldEvidence()
    {
        var prior = Observe(null, Step(1), Epoch);
        prior = Observe(prior, Step(2), Epoch.AddSeconds(2));
        var other = Equipment with { Mount = new("Other mount", true) };
        var status = Phd2GuideOutputStatus.Observe(prior, Step(1) with { Mount = "Other mount" },
            Epoch.AddSeconds(4), other);
        Assert.Equal(1, status!.ConsecutiveMissingOutputs);
        Assert.False(status.Failed);
    }
}
