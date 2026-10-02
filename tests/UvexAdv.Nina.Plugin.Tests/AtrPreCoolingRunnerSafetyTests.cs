using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class AtrPreCoolingRunnerSafetyTests
{
    [Fact]
    public void RecoveryUsesIdleBoundNativeOwnerAndStopsOldCoolingBeforeReconnect()
    {
        var recovery = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "RealObservationStageRunner.AtrCoolingRecovery.cs"));
        Assert.Contains("profileService.ActiveProfile.CameraSettings.Id != expectedId", recovery);
        Assert.Contains("ReferenceEquals(expectedProfile, profileService.ActiveProfile)", recovery);
        Assert.Contains("ReferenceEquals(owner, cameraMediator.GetDevice())", recovery);
        Assert.Contains("info.IsExposing || info.CameraState", recovery);
        Assert.Contains("!cameraMediator.IsFreeToCapture(captureBlock)", recovery);
        Assert.Contains("cameraMediator.RegisterCaptureBlock(captureBlock)", recovery);
        Assert.Contains("cameraMediator.ReleaseCaptureBlock(captureBlock)", recovery);
        var wait = recovery.IndexOf("await oldTask.WaitAsync", StringComparison.Ordinal);
        var disconnect = recovery.IndexOf("await cameraMediator.Disconnect()", StringComparison.Ordinal);
        var connect = recovery.IndexOf("await cameraMediator.Connect()", StringComparison.Ordinal);
        var cool = recovery.IndexOf("cameraMediator.CoolCamera(", StringComparison.Ordinal);
        Assert.True(wait >= 0 && disconnect > wait && connect > disconnect && cool > connect);
        Assert.Contains("token.ThrowIfCancellationRequested()", recovery[disconnect..connect]);
        Assert.Contains("ReadFreshAtrOwnerAsync(owner, expectedId, expectedId, token)", recovery);
        Assert.Contains("stableTemperatureClaimed = false", recovery);
        Assert.DoesNotContain("CaptureImage", recovery);
        Assert.DoesNotContain("telescopeMediator", recovery);
    }
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
