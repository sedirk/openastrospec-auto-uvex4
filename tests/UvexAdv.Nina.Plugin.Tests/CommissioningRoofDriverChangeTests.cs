using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using UvexAdv.Commissioning.Tool;
using UvexAdv.Nina.Plugin;
using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public class CommissioningRoofDriverChangeTests
{
    [Theory]
    [InlineData("other-mount", "new-roof")]
    [InlineData("mount", "No_Device")]
    [InlineData("mount", "")]
    [InlineData("mount", "ASCOM.Simulator.Dome")]
    public void NewPhysicalContextIsNotDriverOnly(string mount, string roof)
    {
        var attestation = new RoofClearanceAttestation(1, "owner-proof", "owner", DateTimeOffset.UtcNow,
            "mount", "old-roof", true, "Physical all-position clearance confirmed.");
        Assert.Throws<InvalidOperationException>(() => CommissioningRoofDriverChange.RequireDriverOnly(attestation, mount, roof));
    }

    [Fact]
    public async Task NoConfirmationCreatesNothing()
    {
        var directory = Path.Combine(Path.GetTempPath(), "uvex-rebind-test-"+Guid.NewGuid());
        await Assert.ThrowsAsync<InvalidOperationException>(() => CommissioningRoofDriverChange.CreateAsync(
            new("missing", new string('a',64), "old", "new", "mount", new string('b',64)), false, directory));
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task ActualHistoricalBundleIsDerivedAndValidatedWithoutChangingAnyOldFileOrProfile()
    {
        var path = Environment.GetEnvironmentVariable("UVEX_BINDING_REPLAY");
        if (string.IsNullOrWhiteSpace(path)) return;
        var bindings = JsonSerializer.Deserialize<CommissioningBindings>(File.ReadAllText(path), ArtifactIO.JsonOptions)!;
        var before = Directory.GetFiles(Path.GetDirectoryName(path)!).ToDictionary(p => p, p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
        var change = CommissioningRoofDriverChange.Inspect(path, "ASCOM.OnStep.Telescope", "replay-only-new-roof", bindings.PresetSha256);
        Assert.NotNull(change);
        // Separate ignored folder, never a live Profile or auto-discovery root.
        var output = Path.Combine(Path.GetTempPath(), "uvex-driver-replay-"+Guid.NewGuid());
        var result = await CommissioningRoofDriverChange.CreateAsync(change, true, output, verifyCurrentPhd2Registry: false);
        var next = JsonSerializer.Deserialize<CommissioningBindings>(File.ReadAllText(result), ArtifactIO.JsonOptions)!;
        Assert.Equal(bindings.NightSetupSha256, next.NightSetupSha256);
        Assert.Equal(bindings.HardwareFingerprintSha256, next.HardwareFingerprintSha256);
        Assert.NotEqual(bindings.PresetSha256, next.PresetSha256);
        var reference = CommissioningRoofDriverChange.ReadReference(result);
        Assert.Equal(next.PresetSha256, reference.PresetSha256);
        Assert.Equal(next.HardwareFingerprintSha256, reference.HardwareFingerprintSha256);
        Assert.Equal(4, reference.GetType().GetProperties().Length);
        foreach (var item in before) Assert.Equal(item.Value, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(item.Key))));
        Assert.Null(CommissioningRoofDriverChange.Inspect(result, "ASCOM.OnStep.Telescope", "replay-only-new-roof", next.PresetSha256));
        await Assert.ThrowsAnyAsync<Exception>(() => CommissioningRoofDriverChange.CreateAsync(change with { BindingsSha256 = new string('f',64) }, true, output));
    }
}
