using UvexAdv.Qhy.Core;
using UvexAdv.Qhy.Service;
using UvexAdv.Qhy.Service.Adapters;

namespace UvexAdv.Qhy.Tests;

public sealed class QhyAdapterIdentityTests
{
    [Fact]
    public async Task SimulatorRejectsWrongStableIdWithoutOrdinalFallback()
    {
        await using var adapter = new SimulatedQhyCameraAdapter(new QhyServiceOptions
        {
            Simulator = true,
            ExpectedStableId = "SIM-EXPECTED",
            ExpectedModel = "QHYminiCam8M",
        });

        var error = await Assert.ThrowsAsync<QhyAdapterException>(() =>
            adapter.ConnectExactAsync("SIM-WRONG", "QHYminiCam8M", CancellationToken.None));

        Assert.Contains("stable-ID mismatch", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(adapter.Status.Connected);
    }

    [Fact]
    public async Task NativeModeRefusesToLoadAnUnpinnedSdkBeforeEnumeration()
    {
        await using var adapter = new NativeQhyCameraAdapter(new QhyServiceOptions
        {
            Simulator = false,
            NativeSdkPath = string.Empty,
            NativeSdkSha256 = string.Empty,
        });

        var error = await Assert.ThrowsAsync<QhyAdapterException>(() =>
            adapter.ConnectExactAsync("configured-exact-id", "QHYminiCam8M", CancellationToken.None));

        Assert.Contains("NativeSdkPath", error.Message, StringComparison.Ordinal);
        Assert.False(adapter.Status.Connected);
    }

    [Fact]
    public async Task NativeAdapterDisposalIsConcurrentSafeAndIdempotentWithoutLoadingSdk()
    {
        var adapter = new NativeQhyCameraAdapter(new QhyServiceOptions
        {
            Simulator = false,
            NativeSdkPath = string.Empty,
            NativeSdkSha256 = string.Empty,
        });

        var disposals = Enumerable.Range(0, 8)
            .Select(_ => adapter.DisposeAsync().AsTask())
            .ToArray();
        await Task.WhenAll(disposals);
        await adapter.DisposeAsync();
        await adapter.DisconnectAsync(CancellationToken.None);

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            adapter.ConnectExactAsync("configured-exact-id", "QHYminiCam8M", CancellationToken.None));
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            adapter.CaptureSingleFrameAsync(new QhyFrameSettings(0.01, 0, 256), CancellationToken.None));
    }

    [Fact]
    public async Task SimulatorReportsAndStrictlySelectsConfiguredFilter()
    {
        await using var adapter = new SimulatedQhyCameraAdapter(new QhyServiceOptions
        {
            Simulator = true,
            ExpectedStableId = "SIM-EXPECTED",
            ExpectedModel = "QHYminiCam8M",
            SimulationDelayMilliseconds = 0,
            NativeFilterPositions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["G"] = 4,
                ["R"] = 5,
            },
        });

        await adapter.ConnectExactAsync("SIM-EXPECTED", "QHYminiCam8M", CancellationToken.None);
        var initial = await adapter.ReadFilterWheelStatusAsync(CancellationToken.None);
        Assert.True(initial.Configured);
        Assert.True(initial.PositionKnown);
        Assert.Equal(4, initial.Position);
        Assert.Equal("G", initial.FilterName);

        var selected = await adapter.SelectFilterAsync("r", CancellationToken.None);
        Assert.Equal(5, selected.Position);
        Assert.Equal("R", selected.FilterName);
        Assert.Equal(selected, adapter.Status.FilterWheel);

        var frame = await adapter.CaptureSingleFrameAsync(
            new QhyFrameSettings(0.01, 10, 256, FilterName: "R"),
            CancellationToken.None);
        Assert.Equal("R", frame.Settings.FilterName);
        await Assert.ThrowsAsync<QhyAdapterException>(() =>
            adapter.CaptureSingleFrameAsync(
                new QhyFrameSettings(0.01, 10, 256, FilterName: "Clear"),
                CancellationToken.None));
    }
}
