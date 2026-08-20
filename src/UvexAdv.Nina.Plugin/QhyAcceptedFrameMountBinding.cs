using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

/// <summary>
/// Dual-ended mount attestation for an accepted QHY acquisition frame.  The
/// first readback is taken before the acquisition job is started and the
/// second immediately after the accepted frame/job completes.  It is not a
/// substitute for the fresh readback required before a later command.
/// </summary>
internal sealed record QhyAcceptedFrameMountBinding(
    int SchemaVersion,
    string ObservationRunId,
    string ActionConfigurationSha256,
    string CommissioningPresetSha256,
    Guid JobId,
    Guid FrameId,
    string FrameSha256,
    DateTimeOffset ExposureStartedUtc,
    DateTimeOffset ExposureEndedUtc,
    G3FrameMountReadback BeforeJob,
    G3FrameMountReadback AfterAcceptedFrame,
    string BindingSha256)
{
    public const int CurrentSchemaVersion = 1;

    public static QhyAcceptedFrameMountBinding Create(
        string observationRunId,
        string actionConfigurationSha256,
        string commissioningPresetSha256,
        Guid jobId,
        Guid frameId,
        string frameSha256,
        DateTimeOffset exposureStartedUtc,
        DateTimeOffset exposureEndedUtc,
        G3FrameMountReadback beforeJob,
        G3FrameMountReadback afterAcceptedFrame)
    {
        var provisional = new QhyAcceptedFrameMountBinding(
            CurrentSchemaVersion,
            observationRunId,
            actionConfigurationSha256,
            commissioningPresetSha256,
            jobId,
            frameId,
            NormalizeHash(frameSha256),
            exposureStartedUtc,
            exposureEndedUtc,
            beforeJob,
            afterAcceptedFrame,
            string.Empty);
        return provisional with { BindingSha256 = provisional.ComputeBindingSha256() };
    }

    public string ComputeBindingSha256()
    {
        var fields = new[]
        {
            SchemaVersion.ToString(CultureInfo.InvariantCulture),
            ObservationRunId,
            ActionConfigurationSha256,
            CommissioningPresetSha256,
            JobId.ToString("D"),
            FrameId.ToString("D"),
            NormalizeHash(FrameSha256),
            ExposureStartedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            ExposureEndedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            BeforeJob.RightAscensionDegrees.ToString("R", CultureInfo.InvariantCulture),
            BeforeJob.DeclinationDegrees.ToString("R", CultureInfo.InvariantCulture),
            BeforeJob.CoordinateEpoch,
            BeforeJob.PierSide,
            BeforeJob.ReportedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            AfterAcceptedFrame.RightAscensionDegrees.ToString("R", CultureInfo.InvariantCulture),
            AfterAcceptedFrame.DeclinationDegrees.ToString("R", CultureInfo.InvariantCulture),
            AfterAcceptedFrame.CoordinateEpoch,
            AfterAcceptedFrame.PierSide,
            AfterAcceptedFrame.ReportedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        };
        var canonical = string.Concat(fields.Select(value => $"{value.Length}:{value}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public GateResult Validate(
        string observationRunId,
        string actionConfigurationSha256,
        string commissioningPresetSha256,
        Guid jobId,
        Guid frameId,
        string frameSha256,
        double maximumSpanArcseconds)
    {
        if (SchemaVersion != CurrentSchemaVersion || !SameHash(BindingSha256, ComputeBindingSha256()))
            return GateResult.Fail("QHY_CAPTURE_MOUNT_BINDING_HASH_INVALID", "The QHY capture mount binding schema or self-hash is invalid.");
        if (!string.Equals(ObservationRunId, observationRunId, StringComparison.Ordinal) ||
            !SameHash(ActionConfigurationSha256, actionConfigurationSha256) ||
            !SameHash(CommissioningPresetSha256, commissioningPresetSha256) ||
            JobId != jobId || FrameId != frameId || !SameHash(FrameSha256, frameSha256))
            return GateResult.Unknown("QHY_CAPTURE_MOUNT_BINDING_CONTEXT_CHANGED", "The QHY capture binding does not match the current run/action/preset/job/frame/hash.");
        if (!Finite(BeforeJob) || !Finite(AfterAcceptedFrame) ||
            !double.IsFinite(maximumSpanArcseconds) || maximumSpanArcseconds <= 0)
            return GateResult.Unknown("QHY_CAPTURE_MOUNT_BINDING_COORDINATE_INVALID", "The QHY dual-ended mount coordinates or span limit are invalid.");
        if (!KnownPier(BeforeJob.PierSide) || !KnownPier(AfterAcceptedFrame.PierSide) ||
            !string.Equals(BeforeJob.PierSide, AfterAcceptedFrame.PierSide, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(BeforeJob.CoordinateEpoch, AfterAcceptedFrame.CoordinateEpoch, StringComparison.Ordinal))
            return GateResult.Unknown("QHY_CAPTURE_MOUNT_BINDING_TOPOLOGY_CHANGED", "Pier side or coordinate epoch changed across QHY acquisition.");
        if (ExposureStartedUtc == default || ExposureEndedUtc < ExposureStartedUtc ||
            BeforeJob.ReportedUtc > ExposureStartedUtc ||
            AfterAcceptedFrame.ReportedUtc < ExposureEndedUtc)
            return GateResult.Unknown("QHY_CAPTURE_MOUNT_BINDING_TIME_INVALID", "The pre-job/post-frame readbacks are not ordered around the accepted QHY exposure.");
        var span = G3AcquisitionMotionPlanner.AngularSeparationArcseconds(
            BeforeJob.RightAscensionDegrees,
            BeforeJob.DeclinationDegrees,
            AfterAcceptedFrame.RightAscensionDegrees,
            AfterAcceptedFrame.DeclinationDegrees);
        if (!double.IsFinite(span) || span > maximumSpanArcseconds + 1e-9)
            return GateResult.Unknown(
                "QHY_CAPTURE_MOUNT_SPAN_EXCEEDED",
                $"The mount moved {span:F2} arcsec across QHY acquisition (limit {maximumSpanArcseconds:F2}); the frame cannot authorize centering or ghost identity.",
                new Dictionary<string, double> { ["mountSpanArcseconds"] = span, ["maximumSpanArcseconds"] = maximumSpanArcseconds });
        return GateResult.Pass(
            "QHY_CAPTURE_MOUNT_BINDING_VALID",
            $"The accepted QHY frame is bracketed by same-epoch/same-pier readbacks spanning {span:F2} arcsec.",
            new Dictionary<string, double> { ["mountSpanArcseconds"] = span, ["maximumSpanArcseconds"] = maximumSpanArcseconds });
    }

    private static bool Finite(G3FrameMountReadback value) =>
        double.IsFinite(value.RightAscensionDegrees) && value.RightAscensionDegrees is >= 0 and < 360 &&
        double.IsFinite(value.DeclinationDegrees) && value.DeclinationDegrees is >= -90 and <= 90 &&
        !string.IsNullOrWhiteSpace(value.CoordinateEpoch) && value.ReportedUtc != default;

    private static bool KnownPier(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !string.Equals(value, "unknown", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(value, "normal", StringComparison.OrdinalIgnoreCase);

    private static bool SameHash(string? left, string? right) =>
        string.Equals(NormalizeHash(left), NormalizeHash(right), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeHash(string? value) =>
        (value ?? string.Empty).Replace("-", string.Empty, StringComparison.Ordinal).Trim().ToUpperInvariant();
}
