using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using UvexAdv.Commissioning.Tool;
using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

internal sealed record RoofDriverChange(string BindingsPath, string BindingsSha256,
    string OldRoof, string NewRoof, string Telescope, string PresetSha256);

/// <summary>Derivative evidence only. Reuses the commissioning builder, never
/// edits a frozen bundle, infers physical clearance, or invokes a device API.</summary>
internal static class CommissioningRoofDriverChange
{
    internal sealed record PresetReference(string PresetPath, string PresetId,
        string PresetSha256, string HardwareFingerprintSha256);

    internal static PresetReference ReadReference(string path)
    {
        var values = CommissioningProfileCatalog.ReadProfileValues(path);
        string Required(string name) => values.TryGetValue(name, out var value) &&
            value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()! : throw new InvalidDataException("Missing preset reference: " + name);
        return new(Required("CommissioningPresetPath"), Required("CommissioningPresetId"),
            Required("CommissioningPresetSha256"), Required("CommissioningHardwareFingerprintSha256"));
    }

    internal static RoofDriverChange? Inspect(string bindingsPath, string currentMount, string currentRoof, string expectedPresetSha)
    {
        var bytes = File.ReadAllBytes(bindingsPath);
        var bindings = JsonSerializer.Deserialize<CommissioningBindings>(bytes, ArtifactIO.JsonOptions)
            ?? throw new InvalidDataException("No commissioning bindings.");
        if (!Same(bindings.PresetSha256, expectedPresetSha)) throw new InvalidDataException("Applied preset differs from selected bundle.");
        var presetBytes = ArtifactIO.ReadAndVerifyAsync(bindings.PresetPath, bindings.PresetSha256).GetAwaiter().GetResult();
        var preset = JsonSerializer.Deserialize<CommissioningPresetContract>(presetBytes, ArtifactIO.JsonOptions)!;
        var clearance = preset.Environment.RoofClearance;
        if (clearance is null || string.Equals(clearance.RoofDeviceId, currentRoof, StringComparison.Ordinal)) return null;
        RequireDriverOnly(clearance, currentMount, currentRoof);
        return new(Path.GetFullPath(bindingsPath), Convert.ToHexString(SHA256.HashData(bytes)),
            clearance.RoofDeviceId, currentRoof, currentMount, bindings.PresetSha256);
    }

    internal static void RequireDriverOnly(RoofClearanceAttestation original, string currentMount, string currentRoof)
    {
        if (RoofClearancePolicy.Validate(true, original, original.TelescopeDeviceId, original.RoofDeviceId, DateTimeOffset.UtcNow).Count != 0 ||
            original.TelescopeDeviceId != currentMount || string.IsNullOrWhiteSpace(currentRoof) ||
            currentRoof is "No_Device" or "NoDevice" || currentRoof.Contains("simulator", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("不能作为仅换屋顶驱动处理：请在下方设备设置核对真实设备；更换赤道仪或物理结构需重新标定净空。");
    }

    internal static async Task<string> CreateAsync(RoofDriverChange change, bool ownerConfirmed,
        string destinationRoot, bool verifyCurrentPhd2Registry = true, CancellationToken token = default)
    {
        if (!ownerConfirmed) throw new InvalidOperationException("Explicit owner confirmation is required.");
        var bytes = await ArtifactIO.ReadAndVerifyAsync(change.BindingsPath, change.BindingsSha256, token);
        var b = JsonSerializer.Deserialize<CommissioningBindings>(bytes, ArtifactIO.JsonOptions)!;
        if (!Same(b.PresetSha256, change.PresetSha256)) throw new InvalidDataException("Applied evidence changed.");
        var oldPreset = await ArtifactIO.ReadAndVerifyAsync(b.PresetPath, b.PresetSha256, token);
        var definitionBytes = await ArtifactIO.ReadAndVerifyAsync(b.MeasurementDefinitionPath, b.MeasurementDefinitionSha256, token);
        var d = JsonSerializer.Deserialize<CommissioningMeasurementDefinition>(definitionBytes, ArtifactIO.JsonOptions)!;
        var clearance = d.Environment.RoofClearance ?? throw new InvalidDataException("No original clearance attestation.");
        RequireDriverOnly(clearance, change.Telescope, change.NewRoof);
        if (clearance.RoofDeviceId != change.OldRoof) throw new InvalidDataException("Roof evidence changed after preview.");
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssZ")+"-"+Guid.NewGuid().ToString("N")[..8];
        var folder = Path.Combine(destinationRoot, "roof-driver-"+stamp);
        Directory.CreateDirectory(folder);
        // Preserve absolute references when the old definition used relative
        // paths. The referenced raw evidence is never copied or modified.
        string Absolute(string p) => string.IsNullOrWhiteSpace(p) ? p : Path.GetFullPath(p, Path.GetDirectoryName(b.MeasurementDefinitionPath)!);
        var next = d with
        {
            PresetId = "ROOF-DRIVER-"+stamp, CreatedUtc = DateTimeOffset.UtcNow,
            Provenance = d.Provenance+$"; operator confirms same physical roof/mount and unchanged clearance: {change.OldRoof} -> {change.NewRoof}; parent {b.PresetSha256}",
            Environment = d.Environment with { RoofClearance = clearance with
            {
                EvidenceId = "OWNER-ROOF-"+stamp, ConfirmedBy = Environment.UserName,
                ConfirmedUtc = DateTimeOffset.UtcNow, RoofDeviceId = change.NewRoof,
                Basis = "Explicit startup-page confirmation: only driver changed; same physical roof/mount and original clearance unchanged. No device action authorized.",
            } },
            Slit = d.Slit with { EvidencePath = Absolute(d.Slit.EvidencePath) },
            SlitWheelIdentity = d.SlitWheelIdentity is null ? null : d.SlitWheelIdentity with
            {
                Fingerprints = d.SlitWheelIdentity.Fingerprints.Select(f => f with
                { EvidencePath = Absolute(f.EvidencePath), ShortExposureEvidencePath = Absolute(f.ShortExposureEvidencePath),
                  LongExposureEvidencePath = Absolute(f.LongExposureEvidencePath) }).ToArray(),
            },
        };
        var definition = await ArtifactIO.WriteJsonAtomicallyAsync(Path.Combine(folder, "commissioning-definition.json"), next, false, token);
        var phdHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(b.Phd2EvidencePath, token)));
        var inputs = new CommissioningInputFiles(definition.AbsolutePath, definition.Sha256,
            b.NightSetupPath, b.NightSetupSha256, b.Phd2EvidencePath, phdHash, b.Phd2ProfileEvidenceSha256);
        var built = await EvidenceBuilders.CreateCommissioningPresetAsync(inputs, Path.Combine(folder, "commissioning-preset.json"),
            false, verifyCurrentPhd2Registry, token);
        var validated = await EvidenceBuilders.ValidateCommissioningPresetAsync(inputs, built.Artifact.AbsolutePath,
            built.Artifact.Sha256, verifyCurrentPhd2Registry, token);
        if (!validated.Valid) throw new InvalidDataException(string.Join("; ", validated.Issues));
        var old = JsonSerializer.Deserialize<CommissioningPresetContract>(oldPreset, ArtifactIO.JsonOptions)!;
        var normalized = built.Preset with { PresetId = old.PresetId, CreatedUtc = old.CreatedUtc,
            Provenance = old.Provenance, Environment = built.Preset.Environment with { RoofClearance = old.Environment.RoofClearance } };
        if (JsonSerializer.Serialize(normalized, ArtifactIO.CanonicalJsonOptions) != JsonSerializer.Serialize(old, ArtifactIO.CanonicalJsonOptions))
            throw new InvalidDataException("Derivative changed more than roof driver provenance; not applied.");
        var values = new Dictionary<string, object?>(b.NinaProfileValues);
        foreach (var key in new[] { "CommissioningPresetPath", "CommissioningPresetId", "CommissioningPresetSha256", "CommissioningHardwareFingerprintSha256" })
            values[key] = built.Bindings.NinaProfileValues[key];
        var result = built.Bindings with { NinaProfileValues = values };
        var saved = await ArtifactIO.WriteJsonAtomicallyAsync(Path.Combine(folder, "site-profile.bindings.json"), result, false, token);
        // Retain provenance and demonstrate original inputs still match their
        // immutable bindings before returning anything eligible for application.
        await ArtifactIO.ReadAndVerifyAsync(change.BindingsPath, change.BindingsSha256, token);
        await ArtifactIO.ReadAndVerifyAsync(b.PresetPath, b.PresetSha256, token);
        await ArtifactIO.ReadAndVerifyAsync(b.MeasurementDefinitionPath, b.MeasurementDefinitionSha256, token);
        await ArtifactIO.WriteJsonAtomicallyAsync(Path.Combine(folder, "driver-change-receipt.json"),
            new { change, ownerConfirmed, createdUtc = DateTimeOffset.UtcNow, validated, hardwareActions = 0 }, false, token);
        return saved.AbsolutePath;
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
