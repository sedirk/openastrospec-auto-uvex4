using UvexAdv.Phd2;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class Phd2RollingCaptureContractTests
{
    [Fact]
    public async Task FullCaptureRequestsThreeAndRollingRequestsOneButReturnsCompleteFifo()
    {
        List<int> requests = [];
        var next = 0;
        Task<IReadOnlyList<Phd2SlitFieldMeasurement>> Capture(int count)
        { requests.Add(count); return Task.FromResult<IReadOnlyList<Phd2SlitFieldMeasurement>>(
            Enumerable.Range(0, count).Select(_ => Sample(++next)).ToArray()); }
        var first = await Phd2PlacementGuideWindowPolicy.CaptureConsecutiveWindowAsync(3, null, Capture, x => x);
        var rolling = await Phd2PlacementGuideWindowPolicy.CaptureConsecutiveWindowAsync(3, first, Capture, x => x);
        Assert.Equal(new[] { 3, 1 }, requests);
        Assert.Equal(new[] { "2", "3", "4" }, rolling.Select(x => x.FrameSha256));
        Assert.True(Phd2PlacementGuideWindowPolicy.HasUsableMeasuredWindow(rolling, [1, 2, 3], true, true));
        var bad = first.ToArray(); bad[2] = bad[2] with { TargetIdentityConfirmed = false };
        var retainedBad = await Phd2PlacementGuideWindowPolicy.CaptureConsecutiveWindowAsync(3, bad, Capture, x => x);
        Assert.False(Phd2PlacementGuideWindowPolicy.HasUsableMeasuredWindow(retainedBad, [1, 2, 3], true, true));
        Assert.False(Phd2PlacementGuideWindowPolicy.HasUsableMeasuredWindow(rolling, [1, 2, 3], false, true));
        Assert.False(Phd2PlacementGuideWindowPolicy.HasUsableMeasuredWindow(rolling, [1, 2, 3], true, false));
    }

    [Fact]
    public async Task OpticalRejectionBreaksRollingWindowAndReacquiresWholeWindow()
    {
        List<int> requests = [];
        Task<IReadOnlyList<Phd2SlitFieldMeasurement>> Capture(int count)
        {
            requests.Add(count);
            return count == 1 ? Task.FromException<IReadOnlyList<Phd2SlitFieldMeasurement>>(new Phd2RollingWindowInterruptedException())
                : Task.FromResult<IReadOnlyList<Phd2SlitFieldMeasurement>>([Sample(5), Sample(6), Sample(7)]);
        }
        var window = await Phd2PlacementGuideWindowPolicy.CaptureConsecutiveWindowAsync(3,
            new[] { Sample(1), Sample(2), Sample(3) }, Capture, x => x);
        Assert.Equal(new[] { 1, 3 }, requests);
        Assert.Equal(new[] { "5", "6", "7" }, window.Select(x => x.FrameSha256));
    }

    [Theory]
    [InlineData("over-capture")] [InlineData("duplicate")] [InlineData("stale")]
    [InlineData("topology")] [InlineData("incomplete")]
    public async Task RollingCaptureRejectsBrokenContractOrStaleWindow(string fault)
    {
        var next = fault switch { "duplicate" => Sample(2), "stale" => Sample(0),
            "topology" => Sample(4) with { TopologyFingerprintSha256 = "changed" }, _ => Sample(4) };
        IReadOnlyList<Phd2SlitFieldMeasurement> previous = fault == "incomplete" ? [Sample(3)] : [Sample(1), Sample(2), Sample(3)];
        await Assert.ThrowsAsync<InvalidOperationException>(() => Phd2PlacementGuideWindowPolicy.CaptureConsecutiveWindowAsync(
            3, previous, _ => Task.FromResult<IReadOnlyList<Phd2SlitFieldMeasurement>>(
                fault == "over-capture" ? [Sample(4), Sample(5), Sample(6)] : [next]), x => x));
    }

    private static Phd2SlitFieldMeasurement Sample(int n) => new(n.ToString(), DateTimeOffset.UnixEpoch.AddSeconds(n),
        "same-topology", new(100, 100), new(102, 100), new(100, 100), 20, true, 2000, false,
        "measured", "flux", 100, "measured", GuidePositionMeasuredInFrame: true);
}
