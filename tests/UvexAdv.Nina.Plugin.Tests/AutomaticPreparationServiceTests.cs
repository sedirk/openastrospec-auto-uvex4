using System.Text.Json;
using UvexAdv.Nina.Plugin;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class AutomaticPreparationServiceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "uvex-preparation-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void DiscoverSeparatesPartialOwnerEvidenceFromCompleteCommissioning()
    {
        var commissioning = Path.Combine(root, "commissioning");
        Directory.CreateDirectory(Path.Combine(commissioning, "station-profiles"));
        File.WriteAllText(Path.Combine(commissioning, "station-profiles", "default.station-profile.json"), "{}");
        File.WriteAllText(Path.Combine(commissioning, "phd2-profile-2.json"), $$"""
            {
              "ProfileId": 2,
              "ProfileName": "c11+slit+2210",
              "Sha256": "{{new string('A', 64)}}"
            }
            """);

        var report = AutomaticPreparationService.Discover(root);

        Assert.True(report.HasStationOperationalProfile);
        Assert.Equal(1, report.Phd2EvidenceCount);
        Assert.False(report.HasCompleteInstallationCalibration);
        Assert.Contains("PHD2 不可变证据 1 份", report.InstallationStatus, StringComparison.Ordinal);
        Assert.Contains("不连接设备", report.InventorySummary, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteDraftIsClearlyNonAuthoritativeAndListsMeasuredEvidence()
    {
        var report = new AutomaticPreparationEvidenceReport(true, 1, 0, 0, 0, [], []);
        var input = new NightSetupPreparationDraftInput(
            "Nova Sge 2026", "PNV J19450648+1822422", "ASCOM.OnStep.Telescope",
            "ATR-REAL", 100, 256, 1, 0, 0, 3840, 2160, -10, 1,
            "G3-REAL", "c11+slit+2210", 2000, 100, 4095,
            "QHY-REAL", 20, 20, 1, 1, 0, 0, 0, 0, null,
            2, null, null, 40, 5, 2,
            "VisibleWide", "Vega", "NinaSafetyStack", false, report);

        var path = AutomaticPreparationService.WriteDraft(
            root,
            input,
            new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero));

        Assert.True(File.Exists(path));
        Assert.True(File.Exists(path + ".sha256"));
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var json = document.RootElement;
        Assert.Equal("OpenAstroSpec.NightSetupPreparationDraft", json.GetProperty("DocumentKind").GetString());
        Assert.Equal("NeedsMeasuredEvidence", json.GetProperty("Status").GetString());
        var unresolved = json.GetProperty("UnresolvedItems").EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.Contains(unresolved, item => item is not null && item.Contains("三", StringComparison.Ordinal) && item.Contains("焦域", StringComparison.Ordinal));
        Assert.Contains(unresolved, item => item is not null && item.Contains("QHYminiCam8M ROI", StringComparison.Ordinal));
        Assert.Contains(unresolved, item => item is not null && item.Contains("schema-5", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
