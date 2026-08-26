using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class AtrPreCoolingRunnerSafetyTests
{
    private static readonly string Source = File.ReadAllText(Path.Combine(
        AppContext.BaseDirectory,
        "Sources",
        "RealObservationStageRunner.cs"));

    [Fact]
    public void RealRunnerStartsDirectPreCoolingAndDefersOnlyTemperatureGate()
    {
        Assert.Contains("StartAtrPreCoolingAsync(context, cancellationToken)", Source, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.Zero", Source, StringComparison.Ordinal);
        Assert.Contains("atrPreCoolingTask = cameraMediator.CoolCamera(", Source, StringComparison.Ordinal);
        Assert.DoesNotContain("await cameraMediator.CoolCamera(", Source, StringComparison.Ordinal);
        Assert.Contains("gate.Code != \"ATR_TEMPERATURE\"", Source, StringComparison.Ordinal);
        Assert.Contains("atrScienceTemperatureRequired", Source, StringComparison.Ordinal);
    }

    [Fact]
    public void FirstAtrProbeWaitsForStableMeasuredTemperature()
    {
        var selectStart = Source.IndexOf(
            "private async Task<StageResult> SelectAtrExposureAsync(",
            StringComparison.Ordinal);
        var captureStart = Source.IndexOf(
            "var probe = await CaptureAtrImageAsync(",
            selectStart,
            StringComparison.Ordinal);
        var waitStart = Source.IndexOf(
            "WaitForAtrScienceTemperatureAsync(context, cancellationToken)",
            selectStart,
            StringComparison.Ordinal);

        Assert.True(selectStart >= 0 && waitStart > selectStart && captureStart > waitStart);
        Assert.Contains("RequiredStableSamples", Source, StringComparison.Ordinal);
        Assert.Contains("camera.TemperatureSetPoint", Source, StringComparison.Ordinal);
        Assert.Contains("camera.CoolerOn", Source, StringComparison.Ordinal);
        Assert.Contains("camera.CoolerPower", Source, StringComparison.Ordinal);
        Assert.DoesNotContain("AtTargetTemp", Source, StringComparison.Ordinal);
    }
}
