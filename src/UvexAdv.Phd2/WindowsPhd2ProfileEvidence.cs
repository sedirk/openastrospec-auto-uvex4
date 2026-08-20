using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace UvexAdv.Phd2;

public sealed record Phd2ProfileBindingRequirement(
    int ProfileId,
    string ProfileName,
    string CameraName,
    string CameraStableId,
    string MountName,
    int? Binning = null,
    int? GainPercent = null);

public sealed record Phd2ProfileBindingSnapshot(
    int ProfileId,
    string ProfileName,
    string CameraName,
    IReadOnlyList<string> CameraStableIds,
    string MountName,
    int? Binning,
    int? GainPercent,
    int? FocalLengthMillimeters,
    int? CameraBitsPerPixel,
    string EvidenceSource,
    string Sha256,
    DateTimeOffset CapturedUtc);

public sealed record Phd2ProfileBindingValidation(
    Phd2ProfileBindingSnapshot? Evidence,
    IReadOnlyList<string> Failures,
    IReadOnlyList<string> IndeterminateReasons)
{
    public Phd2ValidationStatus Status => Failures.Count > 0
        ? Phd2ValidationStatus.Invalid
        : IndeterminateReasons.Count > 0
            ? Phd2ValidationStatus.Indeterminate
            : Phd2ValidationStatus.Valid;
}

/// <summary>
/// Reads the per-user PHD2 profile registry evidence that the event-server API
/// omits. It never enumerates or opens a camera. The evidence binds the PHD2
/// profile to the exact persisted USB instance path, camera settings, and mount.
/// </summary>
public static class WindowsPhd2ProfileEvidence
{
    private const string ProfileRoot = @"Software\StarkLabs\PHDGuidingV2\profile";

    [SupportedOSPlatform("windows")]
    public static Phd2ProfileBindingValidation ReadAndValidate(Phd2ProfileBindingRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        if (requirement.ProfileId < 0)
        {
            return Unknown("PHD2 profile ID must be non-negative.");
        }

        var profilePath = $@"{ProfileRoot}\{requirement.ProfileId}";
        using var profile = Registry.CurrentUser.OpenSubKey(profilePath, writable: false);
        if (profile is null) return Unknown($"PHD2 registry profile HKCU\\{profilePath} was not found.");

        using var camera = profile.OpenSubKey("camera", writable: false);
        using var scope = profile.OpenSubKey("scope", writable: false);
        if (camera is null || scope is null) return Unknown("PHD2 profile camera/scope registry evidence is incomplete.");

        var stableIds = new List<string>();
        using (var hashes = profile.OpenSubKey("cam_hash", writable: false))
        {
            if (hashes is not null)
            {
                foreach (var subkeyName in hashes.GetSubKeyNames().OrderBy(static value => value, StringComparer.Ordinal))
                {
                    using var hash = hashes.OpenSubKey(subkeyName, writable: false);
                    if (hash?.GetValue("whichCamera") is string value && !string.IsNullOrWhiteSpace(value))
                    {
                        stableIds.Add(value.Trim());
                    }
                }
            }
        }

        int? bitsPerPixel = null;
        using (var touptek = camera.OpenSubKey("ToupTek", writable: false))
        {
            bitsPerPixel = ReadInt32(touptek, "bpp");
        }

        int? focalLength;
        using (var frame = profile.OpenSubKey("frame", writable: false))
        {
            focalLength = ReadInt32(frame, "focalLength");
        }

        var raw = new ProfileEvidencePayload(
            requirement.ProfileId,
            ReadString(profile, "name"),
            ReadString(camera, "LastMenuchoice"),
            stableIds.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
            ReadString(scope, "LastMenuChoice"),
            ReadInt32(camera, "binning"),
            ReadInt32(camera, "gain"),
            focalLength,
            bitsPerPixel);
        var canonical = JsonSerializer.Serialize(raw);
        var snapshot = new Phd2ProfileBindingSnapshot(
            raw.ProfileId,
            raw.ProfileName,
            raw.CameraName,
            raw.CameraStableIds,
            raw.MountName,
            raw.Binning,
            raw.GainPercent,
            raw.FocalLengthMillimeters,
            raw.CameraBitsPerPixel,
            $@"HKCU\{profilePath}",
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))),
            DateTimeOffset.UtcNow);
        return Validate(requirement, snapshot);
    }

    public static Phd2ProfileBindingValidation Validate(
        Phd2ProfileBindingRequirement requirement,
        Phd2ProfileBindingSnapshot? evidence)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        if (evidence is null) return Unknown("PHD2 profile binding evidence is unavailable.");

        var failures = new List<string>();
        var unknown = new List<string>();
        if (evidence.ProfileId != requirement.ProfileId) failures.Add($"PHD2 profile ID is {evidence.ProfileId}, expected {requirement.ProfileId}.");
        AddMismatch(failures, "profile name", requirement.ProfileName, evidence.ProfileName);
        AddMismatch(failures, "camera name", requirement.CameraName, evidence.CameraName);
        AddMismatch(failures, "mount name", requirement.MountName, evidence.MountName);

        if (evidence.CameraStableIds.Count == 0)
        {
            unknown.Add("PHD2 profile contains no persisted camera USB instance binding.");
        }
        else if (evidence.CameraStableIds.Count != 1)
        {
            unknown.Add($"PHD2 profile contains {evidence.CameraStableIds.Count} camera instance bindings; the active binding is ambiguous.");
        }
        else if (!string.Equals(evidence.CameraStableIds[0], requirement.CameraStableId, StringComparison.OrdinalIgnoreCase))
        {
            failures.Add($"PHD2 camera instance is '{evidence.CameraStableIds[0]}', expected '{requirement.CameraStableId}'.");
        }

        if (requirement.Binning is { } expectedBinning)
        {
            if (evidence.Binning is null) unknown.Add("PHD2 profile binning is unavailable.");
            else if (evidence.Binning != expectedBinning) failures.Add($"PHD2 profile binning is {evidence.Binning}, expected {expectedBinning}.");
        }
        if (requirement.GainPercent is { } expectedGain)
        {
            if (evidence.GainPercent is null) unknown.Add("PHD2 profile gain is unavailable.");
            else if (evidence.GainPercent != expectedGain) failures.Add($"PHD2 profile gain is {evidence.GainPercent}, expected {expectedGain}.");
        }
        if (string.IsNullOrWhiteSpace(evidence.Sha256)) unknown.Add("PHD2 profile evidence hash is missing.");

        return new Phd2ProfileBindingValidation(evidence, failures, unknown);
    }

    private static void AddMismatch(List<string> failures, string label, string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            failures.Add($"PHD2 {label} is '{actual}', expected '{expected}'.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static string ReadString(RegistryKey key, string name) => key.GetValue(name) as string ?? string.Empty;

    [SupportedOSPlatform("windows")]
    private static int? ReadInt32(RegistryKey? key, string name) => key?.GetValue(name) switch
    {
        int value => value,
        long value when value is >= int.MinValue and <= int.MaxValue => (int)value,
        string value when int.TryParse(value, out var parsed) => parsed,
        _ => null,
    };

    private static Phd2ProfileBindingValidation Unknown(string message) => new(null, [], [message]);

    private sealed record ProfileEvidencePayload(
        int ProfileId,
        string ProfileName,
        string CameraName,
        IReadOnlyList<string> CameraStableIds,
        string MountName,
        int? Binning,
        int? GainPercent,
        int? FocalLengthMillimeters,
        int? CameraBitsPerPixel);
}
