using System.ComponentModel.Composition;
using System.IO;
using System.Windows.Media;
using NINA.Core.Interfaces;
using NINA.Core.Model;
using NINA.Image.ImageAnalysis;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

public sealed class SepStarDetectionResult : StarDetectionResult
{
    public required SepImageMeasurements Measurements { get; init; }
}

public sealed class SepStarDetectionAnalysis : StarDetectionAnalysis
{
    public SepImageMeasurements? Measurements { get; internal set; }
}

/// <summary>Opt-in NINA image-analysis behavior, not a camera or autofocus owner.</summary>
[Export(typeof(IPluggableBehavior))]
public sealed class SepStarDetection : IStarDetection
{
    public string Name => ObservationUiPresentation.Text("OpenAstroSpec SEP 不规则星像（实验性）", "OpenAstroSpec SEP irregular stars (experimental)");
    public string ContentId => typeof(SepStarDetection).FullName!;

    public async Task<StarDetectionResult> Detect(IRenderedImage image, PixelFormat pf, StarDetectionParams p,
        IProgress<ApplicationStatus> progress, CancellationToken token)
    {
        var raw = image.RawImageData;
        if (raw.Properties.IsBayered || pf != PixelFormats.Gray16 || raw.Data.FlatArray is null)
            throw new NotSupportedException(ObservationUiPresentation.Text(
                "SEP 当前仅支持原始单色 16 位图像；不会把 Bayer 马赛克或光谱预览当成星场。",
                "SEP currently requires raw monochrome 16-bit image data, not Bayer mosaics or rendered previews."));
        var config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UVEX-ADV", "star-detection", "runtime.json");
        if (!File.Exists(config))
            throw new InvalidOperationException(ObservationUiPresentation.Text(
                "SEP 独立分析环境尚未配置。请参阅 docs/star-detection-sep.md；未自动安装或更改当前检测器。",
                "SEP analysis runtime is not configured. See docs/star-detection-sep.md; no automatic install or detector fallback."));
        progress?.Report(new ApplicationStatus { Status = ObservationUiPresentation.Text("SEP：测量原始星像区域与包围能量", "SEP: measuring raw source regions and enclosed energy") });
        try
        {
            var width = raw.Properties.Width;
            var height = raw.Properties.Height;
            var depth = raw.Properties.BitDepth;
            if (depth is < 1 or > 16) throw new NotSupportedException("SEP: invalid native bit depth.");
            // Use NINA's declared ADU range, not this particular frame's peak.
            var saturation = (Math.Pow(2, depth) - 1) * 0.999;
            var measured = await new SepStarDetectionClient().DetectAsync(SepStarDetectionClient.ReadRuntime(config),
                width, height, raw.Data.FlatArray, saturation, token).ConfigureAwait(false);
            return ToNinaResult(measured, p);
        }
        finally { progress?.Report(new ApplicationStatus { Status = string.Empty }); }
    }

    public static StarDetectionResult ToNinaResult(SepImageMeasurements measured, StarDetectionParams p)
    {
        // Saturated/blended/edge measurements remain in the worker diagnostics,
        // but cannot silently become valid autofocus points.
        var stars = measured.Sources.Where(s => s.FocusEligible && s.R50 is > 0)
            .Where(s => !p.UseROI || DetectionUtility.InROI(new System.Drawing.Size(measured.Width, measured.Height),
                new System.Drawing.Rectangle(s.Bbox[0], s.Bbox[1], s.Bbox[2], s.Bbox[3]), p.OuterCropRatio, p.InnerCropRatio))
            .OrderByDescending(s => s.Flux).ToList();
        if (p.MatchStarPositions is { Count: > 0 })
        {
            var matched = new List<SepMeasuredSource>();
            // Bounded one-to-one matching: never substitute an arbitrary distant
            // star, reuse one star twice, or throw when a focus frame is empty.
            foreach (var position in p.MatchStarPositions)
            {
                var nearby = stars.Where(s => !matched.Contains(s))
                    .Select(s => (Star: s, Distance: Math.Sqrt(Math.Pow(s.X - position.X, 2) + Math.Pow(s.Y - position.Y, 2))))
                    .Where(s => s.Distance <= 12).OrderBy(s => s.Distance).ToArray();
                if (nearby.Length == 0 || (nearby.Length > 1 && nearby[1].Distance <= nearby[0].Distance + 2)) continue;
                matched.Add(nearby[0].Star);
            }
            // An incomplete reference ensemble does not produce a biased curve point.
            stars = matched.Count == p.MatchStarPositions.Count ? matched : [];
        }
        else if (p.NumberOfAFStars > 0) stars = stars.Take(p.NumberOfAFStars).ToList();
        var hfrs = stars.Select(s => s.R50!.Value).Order().ToArray();
        var median = Median(hfrs);
        return new SepStarDetectionResult
        {
            Measurements = measured,
            Params = p, DetectedStars = stars.Count, AverageHFR = median,
            HFRStdDev = Median(hfrs.Select(x => Math.Abs(x - median)).Order().ToArray()),
            BrightestStarPositions = stars.Select(s => new Accord.Point((float)s.X, (float)s.Y)).ToList(),
            StarList = stars.Select(s => new DetectedStar
            {
                HFR = s.R50!.Value, Position = new Accord.Point((float)s.X, (float)s.Y),
                AverageBrightness = s.Flux / Math.Max(1.0, (double)s.Bbox[2] * s.Bbox[3]),
                MaxBrightness = s.Peak, Background = s.Background,
                BoundingBox = new System.Drawing.Rectangle(s.Bbox[0], s.Bbox[1], s.Bbox[2], s.Bbox[3]),
            }).ToList(),
        };
    }

    private static double Median(double[] values) => values.Length == 0 ? double.NaN
        : (values[(values.Length - 1) / 2] + values[values.Length / 2]) / 2;
    public IStarDetectionAnalysis CreateAnalysis() => new SepStarDetectionAnalysis();
    public void UpdateAnalysis(IStarDetectionAnalysis analysis, StarDetectionParams p, StarDetectionResult result)
    {
        analysis.HFR = result.AverageHFR;
        analysis.HFRStDev = result.HFRStdDev;
        analysis.DetectedStars = result.DetectedStars;
        analysis.StarList = result.StarList;
        if (analysis is SepStarDetectionAnalysis sepAnalysis && result is SepStarDetectionResult sepResult)
            sepAnalysis.Measurements = sepResult.Measurements;
    }
}
