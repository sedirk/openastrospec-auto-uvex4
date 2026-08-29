using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class CoordinateEpochSeparationTests
{
    private static readonly string MainSource = File.ReadAllText(Path.Combine(
        AppContext.BaseDirectory,
        "Sources",
        "RealObservationStageRunner.cs"));

    private static readonly string PairSource = File.ReadAllText(Path.Combine(
        AppContext.BaseDirectory,
        "Sources",
        "RealObservationStageRunner.QhyG3SolvePair.cs"));

    [Fact]
    public void QhySyncReadbackNormalizesEpochBeforeComputingResidual()
    {
        var separation = Slice(
            MainSource,
            "private static double AngularSeparationArcseconds(Coordinates a, Coordinates b)",
            "private static (double RaTangentArcseconds, double DecArcseconds) SignedTangentOffsetArcseconds(");

        Assert.Contains("b.Epoch == a.Epoch ? b : b.Transform(a.Epoch)", separation, StringComparison.Ordinal);
        Assert.Contains("bInAEpoch.RADegrees", separation, StringComparison.Ordinal);
        Assert.Contains("bInAEpoch.Dec", separation, StringComparison.Ordinal);
        Assert.DoesNotContain("var ra2 = b.RADegrees", separation, StringComparison.Ordinal);
        Assert.DoesNotContain("var dec2 = b.Dec", separation, StringComparison.Ordinal);
        Assert.Contains("same-epoch readback", PairSource, StringComparison.Ordinal);
        Assert.Contains("qhyTruthAtReadbackEpoch", PairSource, StringComparison.Ordinal);
    }

    [Fact]
    public void QhySyncCommandUsesCurrentMountReadbackEpochAndPreservesJ2000Evidence()
    {
        var recovery = Slice(
            PairSource,
            "private async Task<QhyMountCoordinateRecoveryResult> RecoverMountCoordinatesFromQhyWcsIfRequiredAsync(",
            "private async Task<QhyPostSyncCatalogSlewResult> SlewToCatalogTargetAfterQhyCoordinateSyncAsync(");

        Assert.Contains("qhyCoordinates.Transform(Epoch.J2000)", recovery, StringComparison.Ordinal);
        Assert.Contains("qhyTruthJ2000.Transform(syncCommandReadback.Epoch)", recovery, StringComparison.Ordinal);
        Assert.Contains("telescopeMediator.Sync(qhySyncCommandCoordinates)", recovery, StringComparison.Ordinal);
        Assert.DoesNotContain("telescopeMediator.Sync(qhyCoordinates)", recovery, StringComparison.Ordinal);
        Assert.Contains("qhyTruthJ2000 = new", recovery, StringComparison.Ordinal);
        Assert.Contains("qhySyncCommand = new", recovery, StringComparison.Ordinal);
        Assert.Contains("qhyTruthJ2000.Transform(after.Epoch)", recovery, StringComparison.Ordinal);
    }

    [Fact]
    public void IncidentFixtureProvesRawEpochNumbersCreateTheFalse1187ArcsecondResidual()
    {
        // QHY/PL3 J2000 truth and N.I.N.A.'s rounded JNOW Sync coordinate from
        // the 2026-08-29 21:06:13 log. They describe the same physical sky
        // direction; comparing their raw numbers recreates the false blocker.
        const double qhyJ2000RaDegrees = 280.52535800746995;
        const double qhyJ2000DecDegrees = 6.557457558873014;
        const double ninaJnowRaDegrees = (18d + 43d / 60d + 25d / 3600d) * 15d;
        const double ninaJnowDecDegrees = 6d + 35d / 60d + 8d / 3600d;

        var rawRaDecResidual = RawAngularSeparationArcseconds(
            ninaJnowRaDegrees,
            ninaJnowDecDegrees,
            qhyJ2000RaDegrees,
            qhyJ2000DecDegrees);

        Assert.InRange(rawRaDecResidual, 1_150, 1_225);
    }

    private static string Slice(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        return source[start..end];
    }

    private static double RawAngularSeparationArcseconds(
        double ra1Degrees,
        double dec1Degrees,
        double ra2Degrees,
        double dec2Degrees)
    {
        var ra1 = ra1Degrees * Math.PI / 180d;
        var ra2 = ra2Degrees * Math.PI / 180d;
        var dec1 = dec1Degrees * Math.PI / 180d;
        var dec2 = dec2Degrees * Math.PI / 180d;
        var cosine = Math.Sin(dec1) * Math.Sin(dec2) +
            Math.Cos(dec1) * Math.Cos(dec2) * Math.Cos(ra1 - ra2);
        return Math.Acos(Math.Clamp(cosine, -1, 1)) * 180d / Math.PI * 3600d;
    }
}
