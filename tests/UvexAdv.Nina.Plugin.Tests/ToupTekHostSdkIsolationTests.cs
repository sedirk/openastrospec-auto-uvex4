using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class ToupTekHostSdkIsolationTests
{
    private static readonly string Source = File.ReadAllText(Path.Combine(
        AppContext.BaseDirectory,
        "Sources",
        "update-touptek-sdk.ps1"));

    [Fact]
    public void NinaSdkUpdaterNeverWritesIntoToupSkyInstallation()
    {
        Assert.Contains("Host = 'N.I.N.A.'", Source, StringComparison.Ordinal);
        Assert.Contains("External\\x64\\ToupTek\\toupcam.dll", Source, StringComparison.Ordinal);
        Assert.DoesNotContain("Program Files\\ToupTek\\ToupSky", Source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Never copy a", Source, StringComparison.Ordinal);
        Assert.Contains("matching", Source, StringComparison.Ordinal);
        Assert.Contains("official ToupSky installer", Source, StringComparison.Ordinal);
    }
}
