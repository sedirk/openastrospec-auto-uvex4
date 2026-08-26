using UvexAdv.Qhy.Core;
using UvexAdv.Qhy.Service;
using UvexAdv.Qhy.Service.Adapters;

namespace UvexAdv.Qhy.Tests;

public sealed class QhyAdapterIdentityTests
{
    [Fact]
    public void IdentityBoundRequiredControlCapabilityRetainsProvenRangeAcrossFrames()
    {
        var capability = QhyControlCapability.Create(6, "gain", 0, 100, 1);

        capability.ValidateRequestedValue(20);

        Assert.Equal(6, capability.ControlId);
        Assert.Equal("gain", capability.Name);
        Assert.Equal(0, capability.Minimum);
        Assert.Equal(100, capability.Maximum);
        Assert.Equal(1, capability.Step);
    }

    [Fact]
    public void IdentityBoundRequiredControlCapabilityRejectsInvalidRangeAndRequest()
    {
        Assert.Throws<QhyAdapterException>(() =>
            QhyControlCapability.Create(6, "gain", 100, 0, 1));
        var capability = QhyControlCapability.Create(6, "gain", 0, 100, 1);
        Assert.Throws<QhyAdapterException>(() => capability.ValidateRequestedValue(double.NaN));
        Assert.Throws<QhyAdapterException>(() => capability.ValidateRequestedValue(101));
    }

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
                ["U"] = 0,
                ["O"] = 1,
                ["H"] = 2,
                ["S"] = 3,
                ["Z"] = 4,
                ["I"] = 5,
                ["R"] = 6,
                ["G"] = 7,
            },
        });

        await adapter.ConnectExactAsync("SIM-EXPECTED", "QHYminiCam8M", CancellationToken.None);
        var initial = await adapter.ReadFilterWheelStatusAsync(CancellationToken.None);
        Assert.True(initial.Configured);
        Assert.True(initial.PositionKnown);
        Assert.Equal(0, initial.Position);
        Assert.Equal("U", initial.FilterName);

        var oxygen = await adapter.SelectFilterAsync("o", CancellationToken.None);
        Assert.Equal(1, oxygen.Position);
        Assert.Equal("O", oxygen.FilterName);

        var hydrogen = await adapter.SelectFilterAsync("h", CancellationToken.None);
        Assert.Equal(2, hydrogen.Position);
        Assert.Equal("H", hydrogen.FilterName);

        var sulfur = await adapter.SelectFilterAsync("s", CancellationToken.None);
        Assert.Equal(3, sulfur.Position);
        Assert.Equal("S", sulfur.FilterName);

        var selected = await adapter.SelectFilterAsync("r", CancellationToken.None);
        Assert.Equal(6, selected.Position);
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
