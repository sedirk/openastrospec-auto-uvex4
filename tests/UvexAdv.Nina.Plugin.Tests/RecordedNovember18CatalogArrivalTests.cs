using System.Security.Cryptography;
using NINA.Astrometry;
using UvexAdv.Observatory;
using UvexAdv.Qhy.Core;
using Xunit;
using Xunit.Abstractions;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class RecordedNovember18CatalogArrivalTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(2094.393062069623, true)] // 10 Cas first arrival, no shared G3 field
    [InlineData(26.696034350583687, false)] // small follow-up: existing evidence gates still apply
    [InlineData(781.4006690492307, false)] // distance alone does not prove no overlap here
    [InlineData(845, false)]
    [InlineData(846, true)]
    [InlineData(double.NaN, true)]
    [InlineData(-1, true)]
    public void EncoderDisplacementIsOnlyAValidHintAcrossUnsharedFields(double displacement, bool needAstrometry)
    {
        Assert.Equal(needAstrometry, G3PostWcsIdentityPolicy.RequiresIndependentArrivalAstrometry(
            displacement, 1920, 1080, .38388));
    }

    // TAN matrices are recorded from offline D80/ASTAP solutions checked
    // against 39 and 35 raw detections respectively; not fitted to the target.
    // Science data are intentionally not shipped with the test suite.
    [Theory]
    [InlineData("00047", "CE7F8BD65DF95892E022BF3D285AD2B1ED54B7F5D4F124E2AFB53EAB8FC91402",
        1.524040088797, 64.22121861595, -2.141590065878e-5, -.0001037546702204,
        .0001046276256021, -2.160885486228e-5, 824, 504, 658, 239)]
    [InlineData("00299", "B15518D0B083C543E086110AC98328D7F834EA4872C2E6F4C3D3C222705E07D3",
        1.506704683591, 64.22037834995, -2.167865494166e-5, -.0001039588012222,
        .0001043734048967, -2.215476620027e-5, 818, 434, 649, 169)]
    public void WholeFieldAstrometrySeparatesTheTrueStarFromTheRepeatableWrongPeak(
        string prefix, string hash, double ra, double dec, double a, double b, double c, double d,
        int wrongX, int wrongY, int rightX, int rightY)
    {
        var root = Environment.GetEnvironmentVariable("UVEX_REPLAY_OBSERVATIONS");
        if (string.IsNullOrWhiteSpace(root)) return;
        const string run = "UVEX-20261118T130738Z-5cbf128115aa40b";
        var file = Assert.Single(Directory.GetFiles(Path.Combine(root, run, "evidence"), $"{prefix}-*.fit"));
        Assert.Equal(hash, Hash(file));
        var read = QhyFitsCodec.Read(file);
        var astrometry = new DetectorWcsResult(ra, dec, [959.5, 539.5], [[a, b], [c, d]], read.Width, read.Height);
        var catalog = new Coordinates(1.61057219282, 64.19615796995, Epoch.J2000, Coordinates.RAType.Degrees);
        var projected = G3WcsTargetProjector.Project(catalog, astrometry, read.Width, read.Height, "verified replay");
        Assert.InRange(projected.X, rightX - 1d, rightX + 1d);
        Assert.InRange(projected.Y, rightY - 1d, rightY + 1d);
        var wrong = new PixelPoint(wrongX, wrongY);
        Assert.InRange(Distance(wrong, projected), 310, 318);
        var frame = new MonochromeFrame(read.Width, read.Height, read.Pixels,
            G3FrameInputPolicy.ResolveEffectiveSaturationAdu(read.Pixels, 4095));
        var candidates = G3StellarFocusAnalyzer.Analyze(frame).Stars;
        var catalogTarget = SlitTargetIdentifier.Identify(frame, candidates, projected, 100, 8, 1.5);
        Assert.NotNull(catalogTarget.Target);
        Assert.InRange(Distance(catalogTarget.Target.Centroid, projected), 0, 16);
        var priorCandidate = SlitTargetIdentifier.Identify(frame, candidates, wrong, 100, 8, 1.5);
        Assert.NotNull(priorCandidate.Target);
        Assert.InRange(Distance(priorCandidate.Target.Centroid, wrong), 0, 12);
        Assert.True(G3PostWcsIdentityPolicy.RequiresIndependentArrivalAstrometry(
            2094.393062069623, frame.Width, frame.Height, astrometry.Pixscale));
        // The corrected target would not have fit the 44 px fine/return envelope.
        Assert.True(Distance(catalogTarget.Target.Centroid, new(817.72, 434.07)) > 44);
        output.WriteLine($"{prefix}: current WCS {projected}, measured {catalogTarget.Target.Centroid}, old selection {priorCandidate.Target.Centroid}.");
        Assert.Equal(hash, Hash(file));
    }

    private static double Distance(PixelPoint a, PixelPoint b) =>
        Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
