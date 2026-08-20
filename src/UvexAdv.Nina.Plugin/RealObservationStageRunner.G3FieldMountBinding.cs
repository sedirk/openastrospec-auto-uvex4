using System.IO;
using NINA.Astrometry;
using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

internal sealed partial class RealObservationStageRunner
{
    private G3FrameMountReadback CaptureG3FrameMountReadback()
    {
        var reported = telescopeMediator.GetCurrentPosition();
        EnsureFiniteReportedCoordinates(reported);
        return new G3FrameMountReadback(
            NormalizeDegrees(reported.RADegrees),
            reported.Dec,
            reported.Epoch.ToString(),
            telescopeMediator.GetInfo().SideOfPier.ToString(),
            DateTimeOffset.UtcNow);
    }

    private G3FieldMountBinding CreateG3FieldMountBinding(
        ObservationContext context,
        string framePath,
        string frameSha256,
        DateTimeOffset frameCompletedUtc,
        G3FrameMountReadback readback)
    {
        if (commissioning is null) throw new InvalidOperationException("Commissioning preset is not loaded.");
        return G3FieldMountBinding.Create(
            context.Plan.ObservationRunId,
            configuration.ActionConfigurationSha256,
            commissioning.Sha256,
            framePath,
            frameSha256,
            frameCompletedUtc,
            readback);
    }

    private async Task<GateResult> ValidateG3FieldMountBindingForMotionAsync(
        ObservationContext context,
        G3FieldState field,
        CancellationToken cancellationToken)
    {
        if (commissioning is null)
            return GateResult.Unknown("COMMISSIONING_PRESET_REQUIRED", "Commissioning is unavailable while validating a G3 field mount binding.");
        if (string.IsNullOrWhiteSpace(field.FramePath) || !File.Exists(field.FramePath))
            return GateResult.Unknown("G3_FIELD_MOUNT_BINDING_FRAME_MISSING", "The G3 field FITS is missing; slit-placement motion is prohibited.");
        string frameSha256;
        try
        {
            frameSha256 = await ComputeFileSha256Async(field.FramePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return GateResult.Unknown("G3_FIELD_MOUNT_BINDING_FRAME_UNREADABLE", $"The G3 field FITS could not be re-hashed: {ex.Message}");
        }
        Coordinates current;
        try
        {
            current = telescopeMediator.GetCurrentPosition();
            EnsureFiniteReportedCoordinates(current);
        }
        catch (Exception ex)
        {
            return GateResult.Unknown("G3_FIELD_MOUNT_BINDING_READBACK_UNAVAILABLE", $"A fresh mount position could not be read: {ex.Message}");
        }
        return G3FieldMountBindingPolicy.ValidateForMotion(
            field.MountBinding,
            context.Plan.ObservationRunId,
            configuration.ActionConfigurationSha256,
            commissioning.Sha256,
            field.FramePath,
            frameSha256,
            NormalizeDegrees(current.RADegrees),
            current.Dec,
            current.Epoch.ToString(),
            telescopeMediator.GetInfo().SideOfPier.ToString(),
            MountCommandArrivalToleranceArcseconds);
    }

    private Task<GateResult> ValidateG3ProbeMountBindingForMotionAsync(
        ObservationContext context,
        G3PlateSolveProbeState probe,
        CancellationToken cancellationToken) =>
        ValidateG3FieldMountBindingForMotionAsync(
            context,
            G3FieldState.Failed(probe.Gate, probe.FramePath, probe.Image, probe.Solve, probe.MountBinding),
            cancellationToken);

    private GateResult ValidateG3SlitSequenceMountBindings(
        ObservationContext context,
        G3SlitIlluminationSequence sequence)
    {
        if (commissioning is null)
            return GateResult.Unknown("COMMISSIONING_PRESET_REQUIRED", "Commissioning is unavailable while validating G3 sequence mount bindings.");
        if (sequence.Frames.Count == 0 || sequence.Frames.Any(frame => frame.MountBinding is null))
            return GateResult.Unknown("G3_SEQUENCE_MOUNT_BINDING_MISSING", "Every OFF/ON/OFF frame requires an immediate capture-time mount binding.");
        var bindings = sequence.Frames.Select(frame => frame.MountBinding!).ToArray();
        foreach (var (frame, binding) in sequence.Frames.Zip(bindings))
        {
            var integrity = G3FieldMountBindingPolicy.ValidateForMotion(
                binding,
                context.Plan.ObservationRunId,
                configuration.ActionConfigurationSha256,
                commissioning.Sha256,
                frame.Capture.Path,
                frame.Sha256,
                binding.RightAscensionDegrees,
                binding.DeclinationDegrees,
                binding.CoordinateEpoch,
                binding.PierSide,
                MountCommandArrivalToleranceArcseconds);
            if (integrity.Disposition != GateDisposition.Passed) return integrity;
        }
        var first = bindings[0];
        if (bindings.Any(binding =>
                !string.Equals(binding.CoordinateEpoch, first.CoordinateEpoch, StringComparison.Ordinal) ||
                !string.Equals(binding.PierSide, first.PierSide, StringComparison.OrdinalIgnoreCase)))
            return GateResult.Unknown("G3_SEQUENCE_MOUNT_TOPOLOGY_CHANGED", "Coordinate epoch or pier side changed within the OFF/ON/OFF sequence.");
        var maximumSpan = 0d;
        for (var i = 0; i < bindings.Length; i++)
        for (var j = i + 1; j < bindings.Length; j++)
        {
            var span = G3AcquisitionMotionPlanner.AngularSeparationArcseconds(
                bindings[i].RightAscensionDegrees,
                bindings[i].DeclinationDegrees,
                bindings[j].RightAscensionDegrees,
                bindings[j].DeclinationDegrees);
            if (!double.IsFinite(span))
                return GateResult.Unknown("G3_SEQUENCE_MOUNT_SPAN_INVALID", "A spherical mount separation inside the OFF/ON/OFF sequence is invalid.");
            maximumSpan = Math.Max(maximumSpan, span);
        }
        return maximumSpan <= MountCommandArrivalToleranceArcseconds + 1e-9
            ? GateResult.Pass(
                "G3_SEQUENCE_MOUNT_BINDINGS_STABLE",
                $"All OFF/ON/OFF capture-time mount bindings share epoch/pier and span {maximumSpan:F2} arcsec.",
                new Dictionary<string, double> { ["maximumSequenceMountSpanArcseconds"] = maximumSpan })
            : GateResult.Unknown(
                "G3_SEQUENCE_MOUNT_SPAN_EXCEEDED",
                $"OFF/ON/OFF capture-time mount positions span {maximumSpan:F2} arcsec (limit {MountCommandArrivalToleranceArcseconds:F2}); slit/target composites cannot authorize movement.",
                new Dictionary<string, double> { ["maximumSequenceMountSpanArcseconds"] = maximumSpan });
    }
}
