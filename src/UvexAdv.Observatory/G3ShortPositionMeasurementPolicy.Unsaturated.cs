namespace UvexAdv.Observatory;

public static partial class G3ShortPositionMeasurementPolicy
{
    // Image-only fallback for a globally detected but ambiguous UNSATURATED
    // short frame. Do not change the general guider's star-ranking policy.
    // Fit every distinct local seed, subtract its local halo/background, and
    // require exactly one compact, unimodal, filled PSF in the WCS window.
    // Brightness ranks neither identity nor the surviving PSFs. An independent
    // frame is mandatory even though the input has no clipped pixels.
    private static G3ShortPositionMeasurement MeasureUnsaturated(MonochromeFrame frame,
        IReadOnlyList<StarCandidate> candidates, TargetIdentification original,
        double recognitionRadius, double minimumSnr)
    {
        const int radius = 16;
        // An unclipped compact PSF includes its measured core: unlike clipped
        // wings, it need not have 12 EXTRA pixels surrounding a discarded core.
        const int minimumContourPixels = 9;
        var seeds = new HashSet<(int X, int Y)>();
        var valid = new List<G3ShortPositionMeasurement>();
        var metrics = new Dictionary<string, double>
        {
            ["shortSaturatedPixels"] = 0,
            ["shortLocalSeedCount"] = 0,
            ["shortValidPsfCount"] = 0,
            ["shortMaximumLocalPeakSigma"] = 0,
            ["shortMaximumPeakAdu"] = 0,
            ["minimumShortUnsaturatedContourPixels"] = minimumContourPixels,
            ["shortShapeRejectedCount"] = 0,
            ["shortSecondaryPeakRejectedCount"] = 0,
            ["shortSupportRejectedCount"] = 0,
            ["shortContourPixelRejectedCount"] = 0,
            ["shortContourBoundaryRejectedCount"] = 0,
            ["shortContourAspectRejectedCount"] = 0,
            ["shortContourHollowRejectedCount"] = 0,
            ["shortContourExtentRejectedCount"] = 0,
            ["maximumShortContourSpreadPixels"] = MaximumContourSpreadPixels,
        };
        foreach (var candidate in candidates.Where(c => Distance(c.Centroid, original.PredictedPoint) <= recognitionRadius))
        {
            var cx = (int)Math.Round(candidate.Centroid.X);
            var cy = (int)Math.Round(candidate.Centroid.Y);
            if (cx <= radius + 4 || cy <= radius + 4 || cx >= frame.Width - radius - 4 || cy >= frame.Height - radius - 4) continue;
            // Detector moments can describe the same peak from several 9x9
            // apertures. Canonicalize the seed, then verify secondary peaks
            // below instead of blindly merging close physical double stars.
            var seed = (X: cx, Y: cy);
            for (var y = cy - 4; y <= cy + 4; y++) for (var x = cx - 4; x <= cx + 4; x++)
                if (frame[x, y] > frame[seed.X, seed.Y]) seed = (x, y);
            // A 9x9 moment aperture may end one pixel short of the real peak.
            // Bounded uphill canonicalization prevents adjacent pixels of that
            // SAME peak becoming multiple supposedly independent candidates.
            for (var step = 0; step < radius; step++)
            {
                var next = seed;
                for (var dy = -1; dy <= 1; dy++) for (var dx = -1; dx <= 1; dx++)
                {
                    var x = seed.X + dx; var y = seed.Y + dy;
                    if (Math.Abs(x - cx) > radius || Math.Abs(y - cy) > radius) continue;
                    if (frame[x, y] > frame[next.X, next.Y]) next = (x, y);
                }
                if (next == seed) break;
                seed = next;
            }
            if (seed.X <= radius || seed.Y <= radius || seed.X >= frame.Width - radius - 1 || seed.Y >= frame.Height - radius - 1 ||
                !IsLocalPeak(frame, seed.X, seed.Y)) continue;
            if (!seeds.Add(seed)) continue;
            cx = seed.X; cy = seed.Y;
            var peak = frame[cx, cy];
            metrics["shortMaximumPeakAdu"] = Math.Max(metrics["shortMaximumPeakAdu"], peak);
            var backgroundSamples = new List<double>();
            for (var dy = -radius; dy <= radius; dy++) for (var dx = -radius; dx <= radius; dx++)
                if (dx * dx + dy * dy is >= 100 and <= radius * radius)
                    backgroundSamples.Add(frame[cx + dx, cy + dy]);
            var background = Median(backgroundSamples);
            var sigma = Math.Max(1, 1.4826 * Median(backgroundSamples.Select(v => Math.Abs(v - background))));
            var signal = peak - background;
            var prominence = signal / sigma;
            metrics["shortMaximumLocalPeakSigma"] = Math.Max(metrics["shortMaximumLocalPeakSigma"], prominence);
            if (!double.IsFinite(prominence) || prominence < minimumSnr || peak >= frame.SaturationLevel) continue;

            var contours = new List<Contour>();
            foreach (var fraction in new[] { .35, .50, .70 })
            {
                var points = ConnectedContour(frame, cx, cy, radius, background + fraction * signal);
                // Pixel sampling can leave only 1--4 pixels above 70% of a
                // genuine compact PSF. Require spatial support from the lower
                // contours, not five pixels independently at EVERY level.
                // A hot pixel still fails the outer (9) and middle (5) support.
                var minimumPixels = fraction < .4 ? minimumContourPixels : fraction < .6 ? 5 : 1;
                if (points.Count < minimumPixels)
                {
                    metrics["shortContourPixelRejectedCount"]++;
                    break;
                }
                if (points.Any(p => Math.Abs(p.X - cx) == radius || Math.Abs(p.Y - cy) == radius))
                {
                    metrics["shortContourBoundaryRejectedCount"]++;
                    break;
                }
                var width = points.Max(p => p.X) - points.Min(p => p.X) + 1;
                var height = points.Max(p => p.Y) - points.Min(p => p.Y) + 1;
                if (Math.Max(width, height) > 2.5 * Math.Min(width, height))
                {
                    metrics["shortContourAspectRejectedCount"]++;
                    break;
                }
                var center = new PixelPoint(points.Average(p => p.X), points.Average(p => p.Y));
                // An annulus has an empty centroid even when its rim contains
                // high-SNR local maxima. It must not become a stellar centre.
                if (frame[(int)Math.Round(center.X), (int)Math.Round(center.Y)] < background + fraction * signal)
                {
                    metrics["shortContourHollowRejectedCount"]++;
                    break;
                }
                contours.Add(new(center, points, Math.Max(width, height)));
            }
            // A threshold edge is pixel-quantized on either side of the PSF;
            // allow ONE pixel of span uncertainty, only for an undersampled
            // (<5-pixel) inner contour. The full outer/middle support, filled
            // centre, aspect, multi-peak veto and independent repeat still gate
            // position. This is not a relaxed focus or fine-placement limit.
            var samplingAllowance = contours.Count == 3 && contours[^1].Points.Count < 5 ? 1 : 0;
            var maximumExtent = contours.Count == 3 ? 2.5 * (contours[^1].Extent + samplingAllowance) : 0;
            if (contours.Count == 3 && contours[0].Extent > maximumExtent)
                metrics["shortContourExtentRejectedCount"]++;
            if (contours.Count != 3 || contours[0].Extent > maximumExtent)
            {
                metrics["shortShapeRejectedCount"]++;
                continue;
            }
            var outer = contours[0].Points;
            // A resolved second peak above the outer-contour signal level is
            // retained as ambiguity, not suppressed by seed canonicalization.
            var peaks = outer.Where(p => IsLocalPeak(frame, (int)p.X, (int)p.Y)).ToArray();
            if (peaks.Any(p => Distance(p, new(cx, cy)) >= 2))
            {
                metrics["shortSecondaryPeakRejectedCount"]++;
                continue;
            }
            var centers = contours.Select(c => c.Center).ToArray();
            var spread = centers.Max(a => centers.Max(b => Distance(a, b)));
            if (spread > MaximumContourSpreadPixels) continue;
            var flux = outer.Sum(p => Math.Max(0, frame[(int)p.X, (int)p.Y] - background));
            var snr = flux / Math.Sqrt(Math.Max(1, flux + outer.Count * sigma * sigma));
            if (outer.Count < minimumContourPixels || snr < minimumSnr)
            {
                metrics["shortSupportRejectedCount"]++;
                continue;
            }
            var centerPoint = new PixelPoint(centers.Average(c => c.X), centers.Average(c => c.Y));
            var sectors = outer.Select(p => Math.Clamp((int)((Math.Atan2(p.Y - centerPoint.Y, p.X - centerPoint.X) + Math.PI) / (2 * Math.PI) * 8), 0, 7)).Distinct().Count();
            if (sectors < 6) continue;
            var positionSpread = spread + .5;
            var residual = Distance(centerPoint, original.PredictedPoint);
            if (residual + positionSpread > recognitionRadius) continue;
            var measuredMetrics = new Dictionary<string, double>
            {
                ["shortLocalBackgroundAdu"] = background, ["shortLocalBackgroundSigmaAdu"] = sigma,
                ["shortLocalPeakSigma"] = prominence, ["shortPsfSnr"] = snr,
                ["shortPsfPixels"] = outer.Count, ["shortPsfSectors"] = sectors,
                ["shortContourSpreadPixels"] = spread, ["shortPositionSpreadPixels"] = positionSpread,
                ["shortOuterContourExtentPixels"] = contours[0].Extent,
                ["shortInnerContourExtentPixels"] = contours[^1].Extent,
                ["shortMiddleContourPixels"] = contours[1].Points.Count,
                ["shortInnerContourPixels"] = contours[^1].Points.Count,
                ["shortInnerSamplingAllowancePixels"] = samplingAllowance,
                ["maximumShortOuterContourExtentPixels"] = maximumExtent,
            };
            var gate = GateResult.Pass("G3_SHORT_UNSATURATED_POSITION_MEASURED",
                "One locally background-subtracted compact unsaturated PSF is measured; an independent frame is still required for coarse WCS refinement, not focus or fine placement.", measuredMetrics);
            valid.Add(new(gate, original with
            {
                Gate = gate,
                Target = candidate with { Centroid = centerPoint, PeakAdu = peak, FluxAdu = flux, SignalToNoise = snr, SaturatedFraction = 0 },
                PredictionResidualPixels = residual,
                UniquenessRatio = double.PositiveInfinity,
                Authority = TargetIdentificationAuthority.StellarCentroid,
            }, true, positionSpread));
        }
        metrics["shortLocalSeedCount"] = seeds.Count;
        metrics["shortValidPsfCount"] = valid.Count;
        if (valid.Count != 1)
        {
            var code = valid.Count == 0 ? "G3_SHORT_UNSATURATED_UNMEASURED" : "G3_SHORT_UNSATURATED_AMBIGUOUS";
            return new(GateResult.Unknown(code,
                $"Unsaturated short frame has {valid.Count} independent compact local PSFs in the WCS window; no saturated core is required and no brightest candidate is substituted.", metrics),
                original, true, 0, valid.Select(v => v.Identification.Target!).ToArray());
        }
        var result = valid[0];
        foreach (var pair in result.Gate.Metrics!) metrics[pair.Key] = pair.Value;
        var resultGate = result.Gate with { Metrics = metrics };
        return result with { Gate = resultGate, Identification = result.Identification with { Gate = resultGate },
            ResolvedCandidates = valid.Select(v => v.Identification.Target!).ToArray() };
    }

    private static double Median(IEnumerable<double> samples)
    {
        var sorted = samples.OrderBy(v => v).ToArray();
        return (sorted[(sorted.Length - 1) / 2] + sorted[sorted.Length / 2]) / 2;
    }

    private static bool IsLocalPeak(MonochromeFrame frame, int x, int y)
    {
        var peak = frame[x, y];
        for (var dy = -1; dy <= 1; dy++) for (var dx = -1; dx <= 1; dx++)
        {
            if (dx == 0 && dy == 0) continue;
            var neighbor = frame[x + dx, y + dy];
            if (neighbor > peak || (neighbor == peak && (dy < 0 || (dy == 0 && dx < 0)))) return false;
        }
        return true;
    }
}
