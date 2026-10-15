using System.Diagnostics;
using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

internal sealed record AtrTemperatureConfirmation(bool Applied, double OriginalTemperatureC,
    double? ConfirmedTemperatureC, DateTimeOffset? ConfirmedUtc, string Code,
    IReadOnlyList<AtrCoolingTelemetry> Samples, GateResult? FailureGate = null);

/// <summary>
/// Before first save only. A ToupTekAlike 0 C metadata sample can originate from
/// an unchecked failed SDK read. Never infer temperature from the requested setpoint.
/// </summary>
internal static class AtrTemperatureMetadataConfirmation
{
    internal const string DriverType = "NINA.Equipment.Equipment.MyCamera.ToupTekAlikeCamera";
    internal const int SampleCount = 3;
    internal const int MaximumReads = 6;
    internal static bool NeedsConfirmation(string? driverType, double nativeTemperature,
        double nativeSetPoint, double target) => driverType == DriverType && nativeTemperature == 0 &&
        double.IsFinite(target) && Math.Abs(target) > AtrCoolingReadinessPolicy.TemperatureToleranceC &&
        double.IsFinite(nativeSetPoint) && Math.Abs(nativeSetPoint - target) <= AtrCoolingReadinessPolicy.SetPointToleranceC;

    internal static async Task<AtrTemperatureConfirmation> ConfirmAsync(string? driverType,
        double nativeTemperature, double nativeSetPoint, double target, string expectedCameraId,
        Func<CancellationToken, Task<AtrCoolingTelemetry>> readOwner,
        Func<CancellationToken, Task> waitBetweenSamples, CancellationToken token)
    {
        var samples = new List<AtrCoolingTelemetry>();
        GateResult? failure = null;
        AtrTemperatureConfirmation Unchanged(string code) => new(false, nativeTemperature, null, null, code, samples.ToArray(), failure);
        if (!NeedsConfirmation(driverType, nativeTemperature, nativeSetPoint, target)) return Unchanged("NATIVE_METADATA_UNCHANGED");
        var started = Stopwatch.GetTimestamp();
        var consecutive = 0;
        try
        {
            for (var i = 0; i < MaximumReads; i++)
            {
                token.ThrowIfCancellationRequested();
                if (i > 0) await waitBetweenSamples(token).ConfigureAwait(false);
                var sample = await readOwner(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                samples.Add(sample);
                // Each SDK read is awaited before doing anything else; do not leave
                // an abandoned task reading the owner during disconnect/restart.
                var gate = AtrCoolingReadinessPolicy.Evaluate(sample, expectedCameraId, target);
                failure = gate;
                if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(12)) return Unchanged("OWNER_TEMPERATURE_READBACK_TIMEOUT");
                if (gate.Disposition != GateDisposition.Passed)
                {
                    consecutive = 0;
                    // Only the same isolated zero-temperature symptom may be
                    // retried. Identity, cooler, setpoint or real warming stop here.
                    if (gate.Code == "ATR_PRECOOLING_IN_PROGRESS" && sample.TemperatureC == 0) continue;
                    return Unchanged("OWNER_TEMPERATURE_READBACK_REJECTED");
                }
                if (++consecutive == SampleCount)
                    return new(true, nativeTemperature, sample.TemperatureC, DateTimeOffset.UtcNow,
                        "OWNER_END_TEMPERATURE_RECONFIRMED", samples.ToArray());
            }
            return Unchanged("OWNER_TEMPERATURE_READBACK_UNCONFIRMED");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            failure = GateResult.Unknown("ATR_COOLING_FRESH_READ_FAILED", "Exact-owner temperature confirmation failed; no recovery or skip authority was inferred.");
            return Unchanged("OWNER_TEMPERATURE_READBACK_FAILED");
        }
    }
}
