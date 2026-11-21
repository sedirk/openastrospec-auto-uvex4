using UvexAdv.Observatory;
using UvexAdv.Phd2;

namespace UvexAdv.Nina.Plugin;

internal sealed partial class RealObservationStageRunner
{
    // Use all ordinary prerequisites, including the mount's real UTC readback.
    // A passive observation cannot dispatch a clock repair from inside its wait.
    private async Task RequirePassivePlacementGatesAsync(ObservationContext context, CancellationToken token)
    {
        await CheckpointAndRejectStaleStageStackAsync(context, token).ConfigureAwait(false);
        var gate = ValidateImmediatePhysicalActionGates(context);
        if (gate.Disposition != GateDisposition.Passed) throw new PhysicalActionGateException(gate);
    }

    private async Task<Phd2DistanceGuardEvidence?> CheckDistanceGuardAsync(ObservationContext context,
        Phd2SlitPlacementSession session, Phd2SlitPlacementCommissioning preset, Phd2IdentityValidation identity,
        Phd2ExactLockPositionResult exact, Phd2StateSnapshot before, Phd2GuidingFrameLostException loss,
        DateTimeOffset deadline, CancellationToken token)
    {
        if (configuration.SequencePlan?.TargetDeadlineUtc is { } t && t < deadline) deadline = t;
        if (configuration.SequencePlan?.DeadlineUtc is { } n && n < deadline) deadline = n;
        if (lastG3Field?.TargetIdentification.Target is null) return null;
        async Task<bool> Binding(CancellationToken ct)
        {
            var current = await phd2.ValidateIdentityAsync(PhdIdentityRequirement(), ct).ConfigureAwait(false);
            if (!current.IsValid || current.Profile != identity.Profile || current.Equipment != identity.Equipment ||
                ValidatePhdProfileBindingEvidence().Disposition != GateDisposition.Passed) return false;
            var cal = await phd2.ValidateCalibrationAsync(preset.CalibrationQualityPolicy.ApplyHardRejectionCeilings(
                PhdCalibrationRequirement(allowNativeCalibration: false)), ct).ConfigureAwait(false);
            var read = await phd2.GetLockPositionAsync(ct).ConfigureAwait(false);
            return cal.IsValid && cal.Calibration.Calibrated && !cal.AllowNativeCalibration &&
                cal.Profile == session.Calibration.Profile && cal.ConnectionEpoch == session.ConnectionEpoch &&
                cal.CalibrationChangeSequence == session.Calibration.CalibrationChangeSequence &&
                read is not null && Phd2DistanceGuardWitness.Distance(read, exact.Verified) <= preset.LockVerificationTolerancePixels;
        }
        return await Phd2DistanceGuardRecovery.ObserveAsync(session, preset, identity, exact, before, loss, deadline,
            (step, stamp, ct) => Task.Run(() => Phd2DistanceGuardWitness.FromLocalOwner(configuration.Phd2Host,
                exact, step, stamp, session.LastMeasurement.Measurement.GuideStar, ct), ct), () => phd2.Snapshot,
            ct => RequirePassivePlacementGatesAsync(context, ct), Binding,
            ct => CapturePhd2GuidingMeasurementsAsync(context, preset, session.Topology, exact.Verified,
                session.LastMeasurement.Measurement.TargetCentroid, session.LastMeasurement.RuntimeSlitLocal,
                session.GuideMode, Math.Max(3, session.Quality.RequiredFreshResidualsPerLockShiftStage), ct,
                referenceGuide: session.LastMeasurement.Measurement.GuideStar, strictPassiveWindow: true), token).ConfigureAwait(false);
    }
}
