using UvexAdv.Qhy.Core;

namespace UvexAdv.Qhy.Tests;

public sealed class QhyCodecAndAnalysisTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "UVEX-ADV-QHY.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void AnalyzerFindsSyntheticStarsAndPreviewIsGrayscalePng()
    {
        var frame = CreateStarFrame();
        var metrics = QhyFrameAnalyzer.Analyze(
            frame,
            new QhyQualityThresholds(MinimumDetectedStars: 2, DetectionSigma: 4));

        Assert.True(metrics.DetectedStars >= 2);
        Assert.NotNull(metrics.MedianFwhmPixels);
        Assert.InRange(metrics.ZeroFraction, 0, 0.001);
        var preview = QhyPreviewEncoder.Encode(Guid.NewGuid(), Guid.NewGuid(), frame, metrics);
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, preview.PngBytes[..8]);
        Assert.Equal(0, preview.PngBytes[25]); // PNG IHDR color type: 0 = grayscale.
    }

    [Fact]
    public void AnalyzerRejectsLowNoiseHotPixelsInsteadOfReportingAHealthyCrowdedField()
    {
        var frame = CreateLowNoiseFewStarFrame();
        var thresholds = new QhyQualityThresholds(
            MinimumDetectedStars: 10,
            MaximumSaturatedFraction: 0.1,
            MinimumTransparency: 0,
            DetectionSigma: 5);

        var metrics = QhyFrameAnalyzer.Analyze(frame, thresholds);

        Assert.InRange(metrics.DetectedStars, 5, 7);
        Assert.Contains("LOW_STAR_COUNT", metrics.QualityFlags);
        Assert.DoesNotContain("STAR_DETECTION_CAPPED", metrics.QualityFlags);
        Assert.False(QhyFrameAnalyzer.PassesAcquisitionGate(metrics, thresholds));
        Assert.InRange(metrics.BackgroundSigmaAdu, 1.0, 3.0);
    }

    [Fact]
    public void AnalyzerTreatsDetectionCapAsIndeterminateInsteadOfHealthy()
    {
        var frame = CreateCrowdedStarFrame();
        var thresholds = new QhyQualityThresholds(
            MinimumDetectedStars: 10,
            MaximumSaturatedFraction: 0.1,
            MinimumTransparency: 0,
            DetectionSigma: 5);

        var metrics = QhyFrameAnalyzer.Analyze(frame, thresholds);

        Assert.Equal(500, metrics.DetectedStars);
        Assert.Contains("STAR_DETECTION_CAPPED", metrics.QualityFlags);
        Assert.False(QhyFrameAnalyzer.PassesAcquisitionGate(metrics, thresholds));
    }

    [Fact]
    public async Task FitsRoundTripPreservesUnsignedPixelsAndIdentityMetadata()
    {
        Directory.CreateDirectory(directory);
        var frame = CreateStarFrame();
        var path = Path.Combine(directory, "frame.fits");
        var jobId = Guid.NewGuid();
        var frameId = Guid.NewGuid();

        var digest = await QhyFitsCodec.WriteAsync(
            path,
            frame,
            jobId,
            "run-42",
            frameId,
            3,
            "PHOTOMETRY",
            "Vega",
            279.23473479,
            38.78368896,
            "ICRS",
            CancellationToken.None);
        var read = QhyFitsCodec.Read(path);

        Assert.Equal(64, digest.Length);
        Assert.Equal(frame.Width, read.Width);
        Assert.Equal(frame.Height, read.Height);
        Assert.Equal(frame.Pixels, read.Pixels);
        Assert.Equal("QHYminiCam8M-test-id", read.Header["CAMERAID"]);
        Assert.Equal("Vega", read.Header["OBJECT"]);
        Assert.Equal("279.23473479", read.Header["RA_DEG"]);
        Assert.Equal("38.78368896", read.Header["DEC_DEG"]);
        Assert.Equal("run-42", read.Header["OBS-RUN"]);
        Assert.Equal("PHOTOMETRY", read.Header["FRAMROLE"]);
        Assert.Equal("256", read.Header["OFFSET"]);
        Assert.False(File.Exists(path + ".partial"));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    internal static QhyFrame CreateStarFrame(int width = 96, int height = 72)
    {
        var pixels = Enumerable.Repeat((ushort)600, width * height).ToArray();
        AddGaussian(pixels, width, height, 23, 20, 11_000, 1.5);
        AddGaussian(pixels, width, height, 54, 42, 9_000, 1.8);
        AddGaussian(pixels, width, height, 77, 27, 7_000, 1.3);
        var started = DateTimeOffset.UtcNow;
        return new QhyFrame(
            width,
            height,
            pixels,
            started,
            started.AddSeconds(1),
            new QhyFrameSettings(1, 10, 256),
            new QhyCameraIdentity("QHYminiCam8M-test-id", "QHYminiCam8M", "test"));
    }

    private static QhyFrame CreateLowNoiseFewStarFrame()
    {
        const int width = 640;
        const int height = 480;
        var random = new Random(0x20260817);
        var pixels = new ushort[width * height];
        for (var index = 0; index < pixels.Length; index++)
        {
            var noise = NextGaussian(random) * 1.45;
            pixels[index] = (ushort)Math.Clamp(Math.Round(401 + noise), 0, ushort.MaxValue);
        }

        var stars = new (int X, int Y, double Amplitude)[]
        {
            (73, 83, 105),
            (171, 337, 82),
            (249, 191, 126),
            (361, 411, 74),
            (487, 113, 92),
            (571, 293, 116),
        };

        // The real low-read-noise frame exposed the former detector weakness:
        // isolated positive pixels were counted as hundreds of stars. Reproduce
        // that detector/fixed-pattern population without checking raw data in.
        for (var y = 12; y < height - 12; y += 18)
        {
            for (var x = 12; x < width - 12; x += 18)
            {
                if (stars.Any(star => Math.Abs(star.X - x) < 12 && Math.Abs(star.Y - y) < 12)) continue;
                pixels[(y * width) + x] = (ushort)(520 + ((x + y) % 31));
            }
        }

        foreach (var star in stars)
        {
            AddGaussian(pixels, width, height, star.X, star.Y, star.Amplitude, 1.5);
        }

        return CreateFrame(width, height, pixels);
    }

    private static QhyFrame CreateCrowdedStarFrame()
    {
        const int width = 580;
        const int height = 270;
        var pixels = Enumerable.Repeat((ushort)600, width * height).ToArray();
        for (var y = 12; y <= 252; y += 16)
        {
            for (var x = 12; x <= 556; x += 16)
            {
                AddGaussian(pixels, width, height, x, y, 5_000, 1.35);
            }
        }

        return CreateFrame(width, height, pixels);
    }

    private static QhyFrame CreateFrame(int width, int height, ushort[] pixels)
    {
        var started = DateTimeOffset.UtcNow;
        return new QhyFrame(
            width,
            height,
            pixels,
            started,
            started.AddSeconds(1),
            new QhyFrameSettings(1, 20, 20),
            new QhyCameraIdentity("QHYminiCam8M-test-id", "QHYminiCam8M", "test"));
    }

    private static double NextGaussian(Random random)
    {
        var first = 1 - random.NextDouble();
        var second = 1 - random.NextDouble();
        return Math.Sqrt(-2 * Math.Log(first)) * Math.Cos(2 * Math.PI * second);
    }

    private static void AddGaussian(ushort[] pixels, int width, int height, int centerX, int centerY, double amplitude, double sigma)
    {
        for (var y = Math.Max(0, centerY - 6); y <= Math.Min(height - 1, centerY + 6); y++)
        {
            for (var x = Math.Max(0, centerX - 6); x <= Math.Min(width - 1, centerX + 6); x++)
            {
                var radiusSquared = ((x - centerX) * (x - centerX)) + ((y - centerY) * (y - centerY));
                var value = amplitude * Math.Exp(-radiusSquared / (2 * sigma * sigma));
                var index = (y * width) + x;
                pixels[index] = (ushort)Math.Clamp(Math.Round(pixels[index] + value), 0, ushort.MaxValue);
            }
        }
    }
}
