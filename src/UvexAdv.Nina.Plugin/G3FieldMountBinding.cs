using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

/// <summary>
/// Immutable capture-time mount attestation for any G3 field that may later
/// participate in slit-placement motion. It is an observation binding, never
/// an optical-axis offset or a substitute for a fresh mount readback.
/// </summary>
internal sealed record G3FieldMountBinding(
    int SchemaVersion,
    string ObservationRunId,
    string ActionConfigurationSha256,
    string CommissioningPresetSha256,
    string FramePath,
    string FrameSha256,
    DateTimeOffset FrameCompletedUtc,
    double RightAscensionDegrees,
    double DeclinationDegrees,
    string CoordinateEpoch,
    string PierSide,
    DateTimeOffset MountReportedUtc,
    string BindingSha256)
{
    public const int CurrentSchemaVersion = 1;

    public static G3FieldMountBinding Create(
        string observationRunId,
        string actionConfigurationSha256,
        string commissioningPresetSha256,
        string framePath,
        string frameSha256,
        DateTimeOffset frameCompletedUtc,
        G3FrameMountReadback readback)
    {
        var provisional = new G3FieldMountBinding(
            CurrentSchemaVersion,
            observationRunId,
            actionConfigurationSha256,
            commissioningPresetSha256,
            Path.GetFullPath(framePath),
            NormalizeHash(frameSha256),
            frameCompletedUtc,
            readback.RightAscensionDegrees,
            readback.DeclinationDegrees,
            readback.CoordinateEpoch,
            readback.PierSide,
            readback.ReportedUtc,
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
            Path.GetFullPath(FramePath),
            NormalizeHash(FrameSha256),
            FrameCompletedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            RightAscensionDegrees.ToString("R", CultureInfo.InvariantCulture),
            DeclinationDegrees.ToString("R", CultureInfo.InvariantCulture),
            CoordinateEpoch,
            PierSide,
            MountReportedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        };
        var canonical = string.Concat(fields.Select(value => $"{value.Length}:{value}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static string NormalizeHash(string value) =>
        value.Replace("-", string.Empty, StringComparison.Ordinal).Trim().ToUpperInvariant();
}

internal sealed record G3FrameMountReadback(
    double RightAscensionDegrees,
    double DeclinationDegrees,
    string CoordinateEpoch,
    string PierSide,
    DateTimeOffset ReportedUtc);

internal static class G3FieldMountBindingPolicy
{
    public static GateResult ValidateForMotion(
        G3FieldMountBinding? binding,
        string observationRunId,
        string actionConfigurationSha256,
        string commissioningPresetSha256,
        string framePath,
        string currentFrameSha256,
        double currentRightAscensionDegrees,
        double currentDeclinationDegrees,
        string currentCoordinateEpoch,
        string currentPierSide,
        double maximumSeparationArcseconds)
    {
        if (binding is null)
            return GateResult.Unknown("G3_FIELD_MOUNT_BINDING_MISSING", "The G3 field has no capture-time mount binding; slit-placement motion is prohibited.");
        if (binding.SchemaVersion != G3FieldMountBinding.CurrentSchemaVersion ||
            !SameHash(binding.BindingSha256, binding.ComputeBindingSha256()))
            return GateResult.Fail("G3_FIELD_MOUNT_BINDING_HASH_INVALID", "The G3 capture-time mount binding schema or self-hash is invalid.");
        if (!string.Equals(binding.ObservationRunId, observationRunId, StringComparison.Ordinal) ||
            !SameHash(binding.ActionConfigurationSha256, actionConfigurationSha256) ||
            !SameHash(binding.CommissioningPresetSha256, commissioningPresetSha256))
            return GateResult.Unknown("G3_FIELD_MOUNT_BINDING_CONTEXT_CHANGED", "The G3 field belongs to another run, action configuration or commissioning preset.");
        string boundPath;
        string actualPath;
        try
        {
            boundPath = Path.GetFullPath(binding.FramePath);
            actualPath = Path.GetFullPath(framePath);
        }
        catch (Exception ex)
        {
            return GateResult.Unknown("G3_FIELD_MOUNT_BINDING_PATH_INVALID", $"The G3 field path cannot be canonicalized: {ex.Message}");
        }
        if (!string.Equals(boundPath, actualPath, StringComparison.OrdinalIgnoreCase) ||
            !SameHash(binding.FrameSha256, currentFrameSha256))
            return GateResult.Fail("G3_FIELD_MOUNT_BINDING_FRAME_CHANGED", "The G3 field path or immutable FITS SHA-256 differs from its capture-time mount binding.");
        if (binding.FrameCompletedUtc == default || binding.MountReportedUtc == default ||
            binding.MountReportedUtc < binding.FrameCompletedUtc)
            return GateResult.Unknown("G3_FIELD_MOUNT_BINDING_TIME_INVALID", "The mount readback is not ordered with the captured G3 frame.");
        if (!FiniteCoordinate(binding.RightAscensionDegrees, binding.DeclinationDegrees) ||
            !FiniteCoordinate(currentRightAscensionDegrees, currentDeclinationDegrees) ||
            !double.IsFinite(maximumSeparationArcseconds) || maximumSeparationArcseconds <= 0)
            return GateResult.Unknown("G3_FIELD_MOUNT_BINDING_COORDINATE_INVALID", "The capture-time/current mount coordinate or drift limit is invalid.");
        if (string.IsNullOrWhiteSpace(binding.CoordinateEpoch) ||
            !string.Equals(binding.CoordinateEpoch, currentCoordinateEpoch, StringComparison.Ordinal))
            return GateResult.Unknown("G3_FIELD_MOUNT_BINDING_EPOCH_CHANGED", "The mount coordinate epoch changed after the G3 field was captured.");
        if (!KnownPier(binding.PierSide) || !KnownPier(currentPierSide) ||
            !string.Equals(binding.PierSide, currentPierSide, StringComparison.OrdinalIgnoreCase))
            return GateResult.Unknown("G3_FIELD_MOUNT_BINDING_PIER_CHANGED", "The mount pier side is unknown or changed after the G3 field was captured.");
        var separation = G3AcquisitionMotionPlanner.AngularSeparationArcseconds(
            binding.RightAscensionDegrees,
            binding.DeclinationDegrees,
            currentRightAscensionDegrees,
            currentDeclinationDegrees);
        if (!double.IsFinite(separation) || separation > maximumSeparationArcseconds + 1e-9)
            return GateResult.Unknown(
                "G3_FIELD_MOUNT_BINDING_STALE",
                $"The fresh mount report is {separation:F2} arcsec from the G3 capture position (limit {maximumSeparationArcseconds:F2} arcsec); discard the field and reacquire before slit placement.",
                new Dictionary<string, double> { ["mountSeparationArcseconds"] = separation, ["maximumSeparationArcseconds"] = maximumSeparationArcseconds });
        return GateResult.Pass(
            "G3_FIELD_MOUNT_BINDING_FRESH",
            $"The immutable G3 FITS and capture-time mount RA/Dec/epoch/pier remain bound within {separation:F2} arcsec.",
            new Dictionary<string, double> { ["mountSeparationArcseconds"] = separation, ["maximumSeparationArcseconds"] = maximumSeparationArcseconds });
    }

    private static bool SameHash(string? left, string? right) =>
        string.Equals(NormalizeHash(left), NormalizeHash(right), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeHash(string? value) =>
        (value ?? string.Empty).Replace("-", string.Empty, StringComparison.Ordinal).Trim();

    private static bool FiniteCoordinate(double ra, double dec) =>
        double.IsFinite(ra) && ra is >= 0 and < 360 && double.IsFinite(dec) && dec is >= -90 and <= 90;

    private static bool KnownPier(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !string.Equals(value, "unknown", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(value, "normal", StringComparison.OrdinalIgnoreCase);
}
