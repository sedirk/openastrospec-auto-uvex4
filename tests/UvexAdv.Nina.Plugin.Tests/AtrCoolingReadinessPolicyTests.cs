using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class AtrCoolingReadinessPolicyTests
{
    private const string CameraId = "ATR585M-EXACT-ID";

    [Fact]
    public void ReadyRequiresMeasuredTemperatureSetPointCoolerAndPower()
    {
        var result = AtrCoolingReadinessPolicy.Evaluate(
            Telemetry(temperature: -9.8, setPoint: -10, coolerOn: true, power: 42),
            CameraId,
            -10);

        Assert.Equal(GateDisposition.Passed, result.Disposition);
        Assert.Equal("ATR_SCIENCE_TEMPERATURE_READY", result.Code);
        Assert.Equal(-9.8, result.Metrics!["temperatureC"]);
    }

    [Fact]
    public void AmbientCameraIsParallelPreCoolingNotAConfigurationFailure()
    {
        var result = AtrCoolingReadinessPolicy.Evaluate(
            Telemetry(temperature: 47.4, setPoint: -10, coolerOn: true, power: 100),
            CameraId,
            -10);

        Assert.Equal(GateDisposition.Indeterminate, result.Disposition);
        Assert.Equal("ATR_PRECOOLING_IN_PROGRESS", result.Code);
        Assert.Contains("47.40", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StrictEqualityAtTargetFlagIsNotAnInput()
    {
        var result = AtrCoolingReadinessPolicy.Evaluate(
            Telemetry(temperature: -9.6, setPoint: -10, coolerOn: true, power: 35),
            CameraId,
            -10);

        Assert.Equal(GateDisposition.Passed, result.Disposition);
    }

    [Theory]
    [InlineData(false, -10, 40, "ATR_COOLER_OFF")]
    [InlineData(true, 0, 40, "ATR_COOLING_SETPOINT_NOT_APPLIED")]
    [InlineData(true, -10, double.NaN, "ATR_COOLING_TELEMETRY_INCOHERENT")]
    public void IncoherentCoolingNeverPasses(bool coolerOn, double setPoint, double power, string expectedCode)
    {
        var result = AtrCoolingReadinessPolicy.Evaluate(
            Telemetry(temperature: -10, setPoint: setPoint, coolerOn: coolerOn, power: power),
            CameraId,
            -10);

        Assert.NotEqual(GateDisposition.Passed, result.Disposition);
        Assert.Equal(expectedCode, result.Code);
    }

    [Fact]
    public void IdentityChangeFailsClosed()
    {
        var result = AtrCoolingReadinessPolicy.Evaluate(
            Telemetry(temperature: -10, setPoint: -10, coolerOn: true, power: 30) with { DeviceId = "OTHER" },
            CameraId,
            -10);

        Assert.Equal(GateDisposition.Failed, result.Disposition);
        Assert.Equal("ATR_COOLING_IDENTITY_CHANGED", result.Code);
    }

    private static AtrCoolingTelemetry Telemetry(
        double temperature,
        double setPoint,
        bool coolerOn,
        double power) => new(
            Connected: true,
            DeviceId: CameraId,
            CanSetTemperature: true,
            CoolerOn: coolerOn,
            TemperatureC: temperature,
            TemperatureSetPointC: setPoint,
            CoolerPowerPercent: power);
}
