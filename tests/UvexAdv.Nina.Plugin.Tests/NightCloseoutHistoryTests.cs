using System.Text.Json;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class NightCloseoutHistoryTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("uvex-night-history-").FullName;
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-15T05:30:00+08:00");
    private string Night => Path.Combine(root, "night-20261114T205116988Z-a155579fcd7b478ca1765fb10ba69c05.json");
    private string Standby => Path.Combine(root, "standby-20261114T205145766Z-2fb938e22cb34054984a38479ee34bd0.json");
    private string Notice => NightCloseoutHistory.ReadNotice(root, Now);
    private static void Write(string path, object body) => File.WriteAllText(path, JsonSerializer.Serialize(body));

    [Theory]
    [InlineData("MonitoringOnly", true)]
    [InlineData("Shutdown", true)]
    [InlineData("Transferred", true)]
    [InlineData("CloseoutFailed", true)]
    [InlineData("Closed", false)]
    public void LegacyStandbyRequiresAPairedPhysicalClosedReceipt(string phase, bool needsNotice)
    {
        Write(Night, new { phase = "Terminal", outcomes = new[] { new { terminal = "done", physicalHardware = true,
            handedToStandby = true, physicalCloseoutConfirmed = false, standbyJournalPath = Standby } } });
        void SaveWatch(string parent, bool real) => Write(Standby, new { parentJournal = parent,
            current = new { Id = Path.GetFileNameWithoutExtension(Standby), Phase = phase, PhysicalHardware = real, Errors = Array.Empty<string>() } });
        SaveWatch(Night, true);
        Assert.Equal(needsNotice, Notice.Length > 0);
        SaveWatch(Night + ".other", true);
        Assert.NotEmpty(Notice);
        SaveWatch(Night, false);
        Assert.NotEmpty(Notice);
    }

    [Fact]
    public async Task InterruptedNightSurvivesRebootEvenAfterDaysAndSimulationDoesNotClearIt()
    {
        Write(Night, new { phase = "Targets", physicalHardware = true, outcomes = Array.Empty<object>() });
        await NightCloseoutHistory.RememberRealNightAsync(Night);
        Write(Path.Combine(root, "night-20261114T215116988Z-00000000000000000000000000000000.json"),
            new { physicalHardware = false, phase = "Terminal", outcomes = Array.Empty<object>() });
        Assert.NotEmpty(Notice);
        Assert.NotEmpty(NightCloseoutHistory.ReadNotice(root, Now.AddDays(10)));
        Write(Night, new { phase = "Terminal", outcomes = new[] {new {terminal = "closed", physicalHardware = true, physicalCloseoutConfirmed = true}} });
        Assert.Empty(Notice);
        Write(Path.Combine(root, "night-20261114T225116988Z-00000000000000000000000000000000.json"),
            new { physicalHardware = true, phase = "Running", outcomes = Array.Empty<object>() });
        Assert.NotEmpty(Notice); // index write was interrupted: do not show the prior success.
    }

    [Fact]
    public async Task MissingCorruptOrRedirectedHistoryIsNotASuccessReceipt()
    {
        Assert.Empty(Notice);
        await NightCloseoutHistory.RememberRealNightAsync(Night);
        Assert.NotEmpty(Notice);
        File.WriteAllText(Night, "[");
        Assert.NotEmpty(Notice);
        File.WriteAllText(Path.Combine(root, "last-real-night.txt"), "../outside.json");
        Assert.NotEmpty(Notice);
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}

public sealed class NightPhysicalStopDiagnosticTests
{
    [Fact]
    public void DisconnectedIsNotEquivalentToBusyOrStopped()
    {
        Assert.Contains("未连接", RealObservationStageRunner.AtrStopIssue(false, true));
        Assert.Contains("未连接", RealObservationStageRunner.MountStopIssue(false, false, false));
        Assert.Contains("曝光", RealObservationStageRunner.AtrStopIssue(true, true));
        Assert.Contains("脉冲", RealObservationStageRunner.MountStopIssue(true, false, true));
        Assert.Null(RealObservationStageRunner.AtrStopIssue(true, false));
        Assert.Null(RealObservationStageRunner.MountStopIssue(true, false, false));
    }
}
