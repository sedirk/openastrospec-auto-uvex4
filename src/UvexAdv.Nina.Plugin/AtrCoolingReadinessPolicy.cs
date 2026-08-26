using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

internal sealed record AtrCoolingTelemetry(
    bool Connected,
    string DeviceId,
    bool CanSetTemperature,
    bool CoolerOn,
    double TemperatureC,
    double TemperatureSetPointC,
    double CoolerPowerPercent);

/// <summary>
/// Evaluates only measured N.I.N.A.-owned ATR telemetry.  In particular it
/// never uses the driver's strict-equality AtTargetTemp convenience flag.
/// </summary>
internal static class AtrCoolingReadinessPolicy
{
    public const double TemperatureToleranceC = 0.5;
    public const double SetPointToleranceC = 0.1;
    public const int RequiredStableSamples = 3;

    public static GateResult Evaluate(
        AtrCoolingTelemetry telemetry,
        string expectedDeviceId,
        double targetTemperatureC)
    {
        if (!telemetry.Connected)
        {
            return GateResult.Unknown("ATR_COOLING_DISCONNECTED", "ATR585M disconnected while cooling or waiting for science readiness.");
        }
        if (!string.Equals(telemetry.DeviceId, expectedDeviceId, StringComparison.Ordinal))
        {
            return GateResult.Fail(
                "ATR_COOLING_IDENTITY_CHANGED",
                $"ATR DeviceId changed to '{telemetry.DeviceId}' while '{expectedDeviceId}' was locked.");
        }
        if (!telemetry.CanSetTemperature)
        {
            return GateResult.Unknown("ATR_COOLING_UNSUPPORTED", "The connected ATR585M does not report controllable cooling.");
        }
        if (!telemetry.CoolerOn)
        {
            return GateResult.Unknown("ATR_COOLER_OFF", "ATR585M cooler is not on.");
        }
        if (!double.IsFinite(telemetry.TemperatureC) ||
            !double.IsFinite(telemetry.TemperatureSetPointC) ||
            !double.IsFinite(telemetry.CoolerPowerPercent) ||
            telemetry.CoolerPowerPercent is < 0 or > 100)
        {
            return GateResult.Unknown(
                "ATR_COOLING_TELEMETRY_INCOHERENT",
                "ATR585M temperature, set-point or cooler-power telemetry is not coherent.");
        }
        if (Math.Abs(telemetry.TemperatureSetPointC - targetTemperatureC) > SetPointToleranceC)
        {
            return GateResult.Unknown(
                "ATR_COOLING_SETPOINT_NOT_APPLIED",
                $"ATR585M reports set-point {telemetry.TemperatureSetPointC:F2} °C; waiting for commanded {targetTemperatureC:F2} °C.");
        }

        var metrics = new Dictionary<string, double>
        {
            ["temperatureC"] = telemetry.TemperatureC,
            ["temperatureSetPointC"] = telemetry.TemperatureSetPointC,
            ["coolerPowerPercent"] = telemetry.CoolerPowerPercent,
            ["targetTemperatureC"] = targetTemperatureC,
        };
        return Math.Abs(telemetry.TemperatureC - targetTemperatureC) <= TemperatureToleranceC
            ? GateResult.Pass(
                "ATR_SCIENCE_TEMPERATURE_READY",
                $"ATR585M is at {telemetry.TemperatureC:F2} °C / {telemetry.TemperatureSetPointC:F2} °C with cooler power {telemetry.CoolerPowerPercent:F0}%.",
                metrics)
            : GateResult.Unknown(
                "ATR_PRECOOLING_IN_PROGRESS",
                $"ATR585M is pre-cooling in parallel: {telemetry.TemperatureC:F2} °C -> {targetTemperatureC:F2} °C (power {telemetry.CoolerPowerPercent:F0}%).",
                metrics);
    }
}
