using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class AtrCoolingReadinessPolicyTests
{
    private const string CameraId = "ATR585M-EXACT-ID";

    [Theory]
    [InlineData("-9.9", "-10", true)]
    [InlineData("-9.5", "-10", true)]
    [InlineData("-9.49", "-10", false)]
    [InlineData("0", "-10", false)]
    [InlineData("NaN", "-10", false)]
    [InlineData("Infinity", "-10", false)]
    [InlineData("-10", "0", false)]
    [InlineData("unavailable", "-10", false)]
    public void SavedFitsMustIndependentlyCarryTheLockedTemperature(string temperature, string setPoint, bool passed)
    {
        var headers = new Dictionary<string, string> { ["CCD-TEMP"] = temperature, ["SET-TEMP"] = setPoint };
        var gate = AtrCoolingReadinessPolicy.EvaluateSavedFrame(headers, -10, GateResult.Pass("OWNER", "Fresh readback"));
        Assert.Equal(passed, gate.Disposition == GateDisposition.Passed);
        if (!passed) Assert.Equal(AtrCoolingReadinessPolicy.SavedFrameInvalidCode, gate.Code);
        Assert.Equal(temperature, headers["CCD-TEMP"]); // Never repair a saved header.
        Assert.All(gate.Metrics!.Values, value => Assert.True(double.IsFinite(value)));
    }

    [Fact]
    public void HeaderCannotSubstituteForBadPostSaveOwnerReadbackAndMissingHeaderCannotUseCachedTemperature()
    {
        var headers = new Dictionary<string, string> { ["CCD-TEMP"] = "-10", ["SET-TEMP"] = "-10" };
        Assert.NotEqual(GateDisposition.Passed, AtrCoolingReadinessPolicy.EvaluateSavedFrame(headers, -10,
            GateResult.Unknown("ATR_PRECOOLING_IN_PROGRESS", "0 C readback")).Disposition);
        headers.Remove("CCD-TEMP");
        Assert.NotEqual(GateDisposition.Passed, AtrCoolingReadinessPolicy.EvaluateSavedFrame(headers, -10,
            GateResult.Pass("OWNER", "-10 C readback")).Disposition);
        headers["CCD-TEMP"] = "0";
        headers["SET-TEMP"] = "0";
        Assert.Equal(GateDisposition.Passed, AtrCoolingReadinessPolicy.EvaluateSavedFrame(headers, 0,
            GateResult.Pass("OWNER", "Explicit 0 C target reached")).Disposition);
    }

    [Fact]
    public void BothProductionFrameKindsCheckSavedTemperatureBeforeAcceptanceCounters()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "RealObservationStageRunner.cs"));
        foreach (var pair in new[] { ("retainedAtrProbeFrames++;", "acceptedAtrProbeFrames++;"),
                                     ("retainedAtrScienceFrames++;", "savedAtrFrames++;" ) })
        {
            var retained = source.IndexOf(pair.Item1, StringComparison.Ordinal);
            var accepted = source.IndexOf(pair.Item2, retained, StringComparison.Ordinal);
            Assert.True(retained > 0 && accepted > retained);
            Assert.Contains("savedTemperature.Disposition != GateDisposition.Passed", source[retained..accepted]);
            Assert.Contains("return new StageResult(savedTemperature)", source[retained..accepted]);
        }
        var verification = source.IndexOf("var temperatureGate = AtrCoolingReadinessPolicy.EvaluateSavedFrame(", StringComparison.Ordinal);
        var publish = source.IndexOf("host.PublishEvidence(", verification, StringComparison.Ordinal);
        Assert.Contains("qualityAccepted = false;", source[verification..publish]);
        Assert.Contains("provenance.Headers", source[verification..publish]);
        Assert.DoesNotContain("WriteAll", source[verification..publish]);
    }

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
