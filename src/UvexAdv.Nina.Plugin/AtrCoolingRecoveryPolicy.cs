using System.Globalization;
using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

internal enum AtrCoolingRecoveryAction { Wait, ReapplyTarget, ReconnectOwner, Exhausted }

/// <summary>Per-run, bounded recovery for unchanged out-of-range telemetry. Zero is never success.</summary>
internal sealed class AtrCoolingRecoveryPolicy
{
    internal static readonly TimeSpan StalledInterval = TimeSpan.FromMinutes(2);
    private DateTimeOffset? unchangedSince;
    private double anchor = double.NaN;
    private DateTimeOffset? postSaveZeroUtc;
    private double confirmedTarget = double.NaN;
    internal int ActionsUsed { get; private set; }

    // A camera already stable BEFORE the shutter and six independent exact-owner
    // zeros after save is not routine cooldown. Expedite the same two idle-only
    // recovery actions; this never approves the saved frame or grants a new exposure.
    internal bool NotePostSaveZero(AtrTemperatureConfirmation result, bool wasStable, string owner,
        double target, DateTimeOffset now)
    {
        if (!wasStable || target >= -AtrCoolingReadinessPolicy.TemperatureToleranceC ||
            result is not { Applied: false, OriginalTemperatureC: 0, Code: "OWNER_TEMPERATURE_READBACK_UNCONFIRMED" } ||
            result.Samples.Count != AtrTemperatureMetadataConfirmation.MaximumReads ||
            !result.Samples.All(s => s.TemperatureC == 0 && s.CoolerPowerPercent is > 0 and <= 100 &&
                AtrCoolingReadinessPolicy.Evaluate(s, owner, target).Code == "ATR_PRECOOLING_IN_PROGRESS"))
            return false;
        confirmedTarget = target;
        postSaveZeroUtc = now;
        unchangedSince = now;
        anchor = 0;
        return true;
    }

    internal AtrCoolingRecoveryAction Observe(GateResult readiness, DateTimeOffset now)
    {
        if (readiness.Code != "ATR_PRECOOLING_IN_PROGRESS" || readiness.Metrics is not { } m ||
            m.GetValueOrDefault("temperatureC", double.NaN) != 0 ||
            m.GetValueOrDefault("targetTemperatureC", double.NaN) != confirmedTarget ||
            now < postSaveZeroUtc || now - postSaveZeroUtc > TimeSpan.FromMinutes(1))
            postSaveZeroUtc = null;
        if (readiness.Disposition == GateDisposition.Passed)
        {
            unchangedSince = null;
            return AtrCoolingRecoveryAction.Wait;
        }
        // Identity, disconnect, unsupported devices and arbitrary faults cannot
        // acquire reconnection authority through this policy.
        if (readiness.Code is not ("ATR_PRECOOLING_IN_PROGRESS" or "ATR_COOLER_OFF" or
            "ATR_COOLING_SETPOINT_NOT_APPLIED" or "ATR_COOLING_TELEMETRY_INCOHERENT"))
            return AtrCoolingRecoveryAction.Wait;
        var temperature = readiness.Metrics?.GetValueOrDefault("temperatureC", double.NaN) ?? double.NaN;
        if (unchangedSince is null || now < unchangedSince ||
            (double.IsFinite(temperature) != double.IsFinite(anchor)) ||
            (double.IsFinite(temperature) && Math.Abs(temperature - anchor) >= 0.2))
        {
            unchangedSince = now;
            anchor = temperature;
            return AtrCoolingRecoveryAction.Wait;
        }
        if (now - unchangedSince < (postSaveZeroUtc.HasValue && ActionsUsed < 2 ? TimeSpan.FromSeconds(15) : StalledInterval))
            return AtrCoolingRecoveryAction.Wait;
        unchangedSince = now;
        return ++ActionsUsed switch
        {
            1 => AtrCoolingRecoveryAction.ReapplyTarget,
            2 => AtrCoolingRecoveryAction.ReconnectOwner,
            _ => AtrCoolingRecoveryAction.Exhausted,
        };
    }

    internal static string DescribeWait(GateResult readiness, TimeSpan elapsed, int stable, CultureInfo culture)
    {
        string Value(string key, string format = "F1") =>
            readiness.Metrics is { } m && m.TryGetValue(key, out var v) && double.IsFinite(v)
                ? v.ToString(format, culture) : "—";
        var duration = $"{(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}";
        return ObservationUiPresentation.Text(
            $"光谱曝光尚未开始：等待 ATR 制冷；温度读回 {Value("temperatureC")}°C，目标 {Value("targetTemperatureC")}°C，功率 {Value("coolerPowerPercent", "F0")}%；已等 {duration}，稳定 {stable}/{AtrCoolingReadinessPolicy.RequiredStableSamples}。",
            $"Spectral exposure has not started: waiting for ATR cooling; readback {Value("temperatureC")}°C, target {Value("targetTemperatureC")}°C, power {Value("coolerPowerPercent", "F0")}%; waiting {duration}, stable {stable}/{AtrCoolingReadinessPolicy.RequiredStableSamples}.", culture);
    }
}
