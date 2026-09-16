using UvexAdv.Phd2;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class Phd2PreExposureReceiptTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-16T13:40:43Z");
    private static readonly string Hash = new('a', 64);
    private static Phd2StateSnapshot Owner => Phd2StateSnapshot.Disconnected with
    {
        IsConnected = true, AppState = Phd2AppState.Guiding, ConnectionEpoch = 1,
        GuideEpoch = 45, LockPosition = new(479, 838),
    };
    private static Phd2PreExposureReceipt Receipt() => new("run", Hash, Now, Owner);

    [Fact]
    public void FreshRecoveryWindowMayServeOnlyTheImmediateNextExposure()
    {
        var receipt = Receipt();
        Assert.True(receipt.TryConsume("run", Hash, Owner, Now.AddSeconds(1), true));
        Assert.False(receipt.TryConsume("run", Hash, Owner, Now.AddSeconds(2), true));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(5.001)]
    [InlineData(600)]
    public void FutureOrStaleWindowCannotSkipFreshSampling(double seconds) =>
        Assert.False(Receipt().TryConsume("run", Hash, Owner, Now.AddSeconds(seconds), true));

    [Fact]
    public void ChangedRunFrameLockEpochPauseOrLossCannotSkipSampling()
    {
        Assert.False(Receipt().TryConsume("other", Hash, Owner, Now, true));
        Assert.False(Receipt().TryConsume("run", new string('b', 64), Owner, Now, true));
        Assert.False(Receipt().TryConsume("run", Hash, Owner, Now, false));
        foreach (var state in new[]
        {
            Owner with { IsConnected = false }, Owner with { AppState = Phd2AppState.LostLock },
            Owner with { AppState = Phd2AppState.Stopped }, Owner with { Phd2Paused = true },
            Owner with { AutomationPaused = true }, Owner with { GuideEpoch = 46 },
            Owner with { ConnectionEpoch = 2 }, Owner with { PendingSettleOperationId = 1 },
            Owner with { LockPosition = new(480, 838) },
        })
        {
            var receipt = Receipt();
            Assert.False(receipt.TryConsume("run", Hash, state, Now, true));
            Assert.False(receipt.TryConsume("run", Hash, Owner, Now, true));
        }
    }

    [Fact]
    public void ProductionRecoveryProducesReceiptAndCaptureConsumesItBeforeExposure()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "RealObservationStageRunner.cs"));
        var capture = source[source.IndexOf("private async Task<AtrCapture> CaptureAtrImageAsync(", StringComparison.Ordinal)..];
        Assert.Contains("preExposureReceipt = null", capture);
        Assert.Contains("receipt?.TryConsume", capture);
        Assert.Contains("if (!usedRecoveryWindow)", capture);
        Assert.Contains("VerifyWindSampledGuidingBeforeAtrAsync", capture);
        Assert.True(capture.IndexOf("if (!IsGuidingStable())", StringComparison.Ordinal) <
            capture.IndexOf("imagingMediator.CaptureImage", StringComparison.Ordinal));
        var placement = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "RealObservationStageRunner.Phd2SlitPlacement.cs"));
        Assert.Contains("catch (Phd2GuidingFrameLostException ex)", placement);
        Assert.Contains("PhysicalActionGateException(GateResult.Unknown(\"GUIDING_LOST\", ex.Message))", placement);
    }
}
