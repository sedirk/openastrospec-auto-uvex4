namespace UvexAdv.Observatory;

/// <summary>
/// Observable LED support along the registered slit, relative to its acquisition
/// anchor. These are detection-limited bounds, NOT independently resolved physical
/// aperture endpoints. They may extend display/guide exclusion, never placement.
/// </summary>
public sealed record SlitIlluminationExtent(
    double StartOffsetPixels, double EndOffsetPixels, double EndpointUncertaintyPixels,
    bool StartImageLimited, bool EndImageLimited)
{
    public bool IsValid => double.IsFinite(StartOffsetPixels) && double.IsFinite(EndOffsetPixels) &&
        StartOffsetPixels < 0 && EndOffsetPixels > 0 &&
        double.IsFinite(EndpointUncertaintyPixels) && EndpointUncertaintyPixels >= 0;
    public double LengthPixels => EndOffsetPixels - StartOffsetPixels;
}

public sealed record SlitIlluminationExtentAnalysis(GateResult Gate, SlitIlluminationExtent? Extent);

/// <summary>
/// Searches the detector, not the old seed-length ROI. Robust 64-pixel longitudinal
/// bins expose faint ends without spending the bright reflection's FWHM as a width
/// or moving its commissioned along-slit anchor. OFF subtraction and paired flanks
/// remove fixed stars and illumination gradients. Only anchor-connected support
/// survives; detached glints cannot extend the line.
/// </summary>
public static class SlitIlluminationExtentAnalyzer
{
    private const int Step = 8;
    private const int HalfBin = 32;
    private const int NormalRadius = 26;
    private const double MinimumSnr = 5;

    public static SlitIlluminationExtentAnalysis Analyze(
        MonochromeFrame shortOff, MonochromeFrame shortOn,
        MonochromeFrame longOff, MonochromeFrame longOn, SlitGeometry registeredEdge)
    {
        ArgumentNullException.ThrowIfNull(registeredEdge);
        var frames = new[] { shortOff, shortOn, longOff, longOn };
        foreach (var frame in frames) ArgumentNullException.ThrowIfNull(frame);
        if (frames.Any(f => f.Width != shortOff.Width || f.Height != shortOff.Height) ||
            !double.IsFinite(registeredEdge.AcquisitionPoint.X) ||
            !double.IsFinite(registeredEdge.AcquisitionPoint.Y) ||
            !double.IsFinite(registeredEdge.AngleDegrees) ||
            !double.IsFinite(registeredEdge.LengthPixels) || registeredEdge.LengthPixels <= 0)
            throw new ArgumentException("Same-detector frames and finite registered slit geometry are required.");

        var angle = registeredEdge.AngleDegrees * Math.PI / 180;
        var vx = Math.Cos(angle); var vy = Math.Sin(angle);
        var nx = -vy; var ny = vx;
        // The detector diagonal is the absolute bound; no historical length cap.
        var steps = (int)Math.Ceiling(Math.Sqrt((double)shortOff.Width * shortOff.Width +
            (double)shortOff.Height * shortOff.Height) / Step);
        var bins = new List<Bin>();
        for (var i = -steps; i <= steps; i++)
        {
            var along = i * Step;
            bool InFrame(double t, double normal)
            {
                var x = registeredEdge.AcquisitionPoint.X + vx * t + nx * normal;
                var y = registeredEdge.AcquisitionPoint.Y + vy * t + ny * normal;
                return x >= 1 && x < shortOff.Width - 2 && y >= 1 && y < shortOff.Height - 2;
            }
            if (!InFrame(along - HalfBin, -NormalRadius) || !InFrame(along + HalfBin, -NormalRadius) ||
                !InFrame(along - HalfBin, NormalRadius) || !InFrame(along + HalfBin, NormalRadius)) continue;
            var shortScore = Score(shortOff, shortOn, along);
            var longScore = Score(longOff, longOn, along);
            bins.Add(new Bin(along, Math.Max(shortScore, longScore)));
        }
        var anchor = bins.FindIndex(b => b.Along == 0);
        if (anchor < 0 || bins[anchor].Snr < MinimumSnr)
            return Unknown("SLIT_LED_EXTENT_ANCHOR_UNMEASURED", "The full-length LED search did not measure the registered anchor; no visible-extent estimate was adopted.");

        int Walk(int direction)
        {
            var last = anchor; var gaps = 0;
            for (var i = anchor + direction; i >= 0 && i < bins.Count; i += direction)
            {
                if (bins[i].Snr >= MinimumSnr) { last = i; gaps = 0; }
                else if (++gaps >= 2) break;
            }
            return last;
        }
        var first = Walk(-1); var last = Walk(1);
        var start = bins[first].Along - Step / 2d; var end = bins[last].Along + Step / 2d;
        // A local reflection/spot at the anchor is not a finite line.
        var minimumSide = Math.Min(48, registeredEdge.LengthPixels / 4);
        if (-start < minimumSide || end < minimumSide)
            return Unknown("SLIT_LED_EXTENT_TOO_SHORT", "Only a local illuminated patch was measured; detached patches and the old seed length cannot supply missing endpoints.");
        var extent = new SlitIlluminationExtent(start, end, HalfBin + Step / 2d,
            first == 0, last == bins.Count - 1);
        return new SlitIlluminationExtentAnalysis(GateResult.Pass("SLIT_LED_VISIBLE_EXTENT_MEASURED",
            $"Anchor-connected LED support spans {extent.LengthPixels:F0}px; endpoints are sensitivity-limited (spatial resolution ±{extent.EndpointUncertaintyPixels:F0}px), not physical endcaps. Placement anchor and commissioned usable length are unchanged.",
            new Dictionary<string, double>
            {
                ["visibleStartOffsetPixels"] = start, ["visibleEndOffsetPixels"] = end,
                ["visibleLengthPixels"] = extent.LengthPixels, ["commissionedUsableLengthPixels"] = registeredEdge.LengthPixels,
                ["endpointSpatialResolutionPixels"] = extent.EndpointUncertaintyPixels,
                ["anchorSnr"] = bins[anchor].Snr, ["minimumBinSnr"] = MinimumSnr,
                ["startImageLimited"] = extent.StartImageLimited ? 1 : 0,
                ["endImageLimited"] = extent.EndImageLimited ? 1 : 0,
                ["physicalEndpointsProven"] = 0, ["placementAuthorityExpanded"] = 0,
            }), extent);

        double Score(MonochromeFrame off, MonochromeFrame on, double along)
        {
            var cut = new double[NormalRadius * 2 + 1, HalfBin];
            var flankDifferences = new List<double>();
            for (var normal = -NormalRadius; normal <= NormalRadius; normal++)
            {
                var previous = double.NaN;
                for (var j = 0; j < HalfBin; j++)
                {
                    var t = along - HalfBin + j * 2;
                    var x = registeredEdge.AcquisitionPoint.X + vx * t + nx * normal;
                    var y = registeredEdge.AcquisitionPoint.Y + vy * t + ny * normal;
                    var value = Difference(off, on, x, y);
                    cut[normal + NormalRadius, j] = value;
                    if (Math.Abs(normal) >= 18 && double.IsFinite(value) && double.IsFinite(previous))
                        flankDifferences.Add(value - previous);
                    previous = value;
                }
            }
            if (flankDifferences.Count < 100) return 0;
            var median = Median(flankDifferences);
            var noise = Math.Max(2, 1.4826 * Median(flankDifferences.Select(d => Math.Abs(d - median))) / Math.Sqrt(2));
            var best = 0d;
            for (var shift = -6; shift <= 6; shift++)
            {
                var left = new List<double>(); var right = new List<double>();
                for (var j = 0; j < HalfBin; j++)
                {
                    double Mean(int low, int high)
                    {
                        var sum = 0d;
                        for (var n = low; n <= high; n++)
                        {
                            var value = cut[n + shift + NormalRadius, j];
                            if (!double.IsFinite(value)) return double.NaN;
                            sum += value;
                        }
                        return sum / (high - low + 1);
                    }
                    var core = Mean(-2, 2); var l = Mean(-14, -8); var r = Mean(8, 14);
                    if (!double.IsFinite(core) || !double.IsFinite(l) || !double.IsFinite(r)) continue;
                    left.Add(core - l); right.Add(core - r);
                }
                if (left.Count < HalfBin * 0.75) continue;
                // Factor two discounts interpolation/spatial correlation; either
                // bright flank alone (gradient, shoulder) cannot establish a ridge.
                var sigma = Math.Max(1, noise * Math.Sqrt(1d / 5 + 1d / 7) / Math.Sqrt(left.Count / 2d));
                best = Math.Max(best, Math.Min(Median(left), Median(right)) / sigma);
            }
            return best;
        }
    }

    private static SlitIlluminationExtentAnalysis Unknown(string code, string message) =>
        new(GateResult.Unknown(code, message), null);
    private sealed record Bin(double Along, double Snr);
    private static double Median(IEnumerable<double> values)
    {
        var a = values.Order().ToArray();
        return a.Length % 2 == 0 ? (a[a.Length / 2 - 1] + a[a.Length / 2]) / 2 : a[a.Length / 2];
    }
    private static double Difference(MonochromeFrame off, MonochromeFrame on, double x, double y)
    {
        var ix = (int)Math.Floor(x); var iy = (int)Math.Floor(y);
        var fx = x - ix; var fy = y - iy;
        var value = 0d;
        for (var j = 0; j < 2; j++)
        for (var i = 0; i < 2; i++)
        {
            var a = off[ix + i, iy + j]; var b = on[ix + i, iy + j];
            if (a >= off.SaturationLevel || b >= on.SaturationLevel) return double.NaN;
            value += (b - a) * (i == 0 ? 1 - fx : fx) * (j == 0 ? 1 - fy : fy);
        }
        return value;
    }
}
