namespace UvexAdv.Observatory;

public sealed record G3MosaicInput(MonochromeFrame Frame, G3SimilarityTransform ReferenceToFrame,
    string SourceId, string SourceSha256, double ExposureSeconds = 1);
public sealed record G3MosaicComposition(int Width, int Height, ushort[] Pixels,
    PixelPoint ReferencePixelOffset, double CoveredFraction,
    IReadOnlyDictionary<string, int> SourceContributions, IReadOnlyList<string> SourceSha256)
{
    public bool DerivedNotRaw => true;
    public PixelPoint ReferenceToMosaic(PixelPoint point) => new(point.X - ReferencePixelOffset.X, point.Y - ReferencePixelOffset.Y);
    public PixelPoint MosaicToReference(PixelPoint point) => new(point.X + ReferencePixelOffset.X, point.Y + ReferencePixelOffset.Y);
}

/// <summary>
/// Warps actual input pixels using independently validated registration. Never draws stars
/// from mount coordinates or interpolates a missing registration edge. Output is a disposable
/// derived solver image, not a raw observation and not WCS until the configured solver succeeds.
/// </summary>
public static class G3MosaicComposer
{
    public static G3MosaicComposition Compose(IReadOnlyList<G3MosaicInput> inputs, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (inputs.Count is < 2 or > 16) throw new ArgumentOutOfRangeException(nameof(inputs), "Two to sixteen authenticated registered images are required.");
        if (inputs.Select(i => i.SourceId).Distinct(StringComparer.Ordinal).Count() != inputs.Count ||
            inputs.Select(i => i.SourceSha256).Distinct(StringComparer.OrdinalIgnoreCase).Count() != inputs.Count)
            throw new ArgumentException("Repeated source files cannot create independent mosaic evidence.");
        foreach (var input in inputs)
        {
            if (string.IsNullOrWhiteSpace(input.SourceId) || input.SourceSha256.Length != 64 ||
                !input.SourceSha256.All(Uri.IsHexDigit) || !double.IsFinite(input.ExposureSeconds) || input.ExposureSeconds <= 0 ||
                !double.IsFinite(input.ReferenceToFrame.Scale) || input.ReferenceToFrame.Scale is < .9 or > 1.1 ||
                !double.IsFinite(input.ReferenceToFrame.X) || !double.IsFinite(input.ReferenceToFrame.Y) ||
                Math.Abs(input.ReferenceToFrame.RotationDegrees) > 15)
                throw new ArgumentException("Invalid source/geometry provenance for derived mosaic.");
        }
        var corners = inputs.SelectMany(i => new[] { new PixelPoint(0, 0), new(i.Frame.Width, 0),
            new(i.Frame.Width, i.Frame.Height), new(0, i.Frame.Height) }.Select(i.ReferenceToFrame.Inverse().Apply)).ToArray();
        if (corners.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y) || Math.Abs(p.X) > 100000 || Math.Abs(p.Y) > 100000))
            throw new ArgumentException("Derived mosaic transform is outside the bounded pixel-coordinate envelope.");
        var offset = new PixelPoint(Math.Floor(corners.Min(p => p.X)), Math.Floor(corners.Min(p => p.Y)));
        var spanWidth = Math.Ceiling(corners.Max(p => p.X)) - offset.X;
        var spanHeight = Math.Ceiling(corners.Max(p => p.Y)) - offset.Y;
        if (spanWidth > 10000 || spanHeight > 10000)
            throw new ArgumentException("Derived mosaic exceeds the bounded image allocation envelope.");
        var width = checked((int)spanWidth);
        var height = checked((int)spanHeight);
        // A derived canvas may include one background-only border pixel. This is
        // NOT cropping/resampling a raw input; it lets the normal PL3 bin2 path use
        // complete blocks. Reference origin and all measured pixel coordinates stay fixed.
        width = checked((width + 1) / 2 * 2);
        height = checked((height + 1) / 2 * 2);
        if (width is < 3 or > 10000 || height is < 3 or > 10000 || (long)width * height > 16_000_000)
            throw new ArgumentException("Derived mosaic exceeds the bounded image allocation envelope.");
        var sums = new double[checked(width * height)];
        var weights = new byte[sums.Length];
        var contributions = new Dictionary<string, int>(StringComparer.Ordinal);
        // Normalize exposure/background for the SOLVER ONLY. Raw images remain untouched.
        var backgrounds = inputs.Select(i => Background(i.Frame)).ToArray();
        var exposure = inputs.Min(i => i.ExposureSeconds);
        var outputBackground = backgrounds.Order().ElementAt(backgrounds.Length / 2);
        for (var n = 0; n < inputs.Count; n++)
        {
            var input = inputs[n]; var used = 0;
            for (var y = 0; y < height; y++)
            {
                token.ThrowIfCancellationRequested();
                for (var x = 0; x < width; x++)
                {
                    var p = input.ReferenceToFrame.Apply(new(x + offset.X, y + offset.Y));
                    if (p.X < 0 || p.Y < 0 || p.X > input.Frame.Width - 1 || p.Y > input.Frame.Height - 1) continue;
                    var value = Interpolate(input.Frame, p);
                    var index = y * width + x;
                    sums[index] += (value - backgrounds[n]) * exposure / input.ExposureSeconds;
                    weights[index]++; used++;
                }
            }
            if (used == 0) throw new InvalidOperationException("Registered source made no contribution to mosaic.");
            contributions.Add(input.SourceId, used);
        }
        var output = new ushort[sums.Length]; var covered = 0;
        for (var i = 0; i < output.Length; i++)
        {
            if ((i & 65535) == 0) token.ThrowIfCancellationRequested();
            if (weights[i] != 0) covered++;
            var value = weights[i] == 0 ? outputBackground : sums[i] / weights[i] + outputBackground;
            output[i] = (ushort)Math.Clamp(Math.Round(value), 0, 65535);
        }
        return new(width, height, output, offset, covered / (double)output.Length,
            contributions, inputs.Select(i => i.SourceSha256).ToArray());
    }

    private static double Background(MonochromeFrame frame)
    {
        var sample = new List<ushort>();
        for (var y = 0; y < frame.Height; y += 16)
        for (var x = 0; x < frame.Width; x += 16) sample.Add(frame[x, y]);
        sample.Sort(); return sample[sample.Count / 2];
    }
    private static double Interpolate(MonochromeFrame frame, PixelPoint p)
    {
        var x = (int)p.X; var y = (int)p.Y;
        var nextX = Math.Min(x + 1, frame.Width - 1); var nextY = Math.Min(y + 1, frame.Height - 1);
        var dx = p.X - x; var dy = p.Y - y;
        return (1 - dy) * ((1 - dx) * frame[x, y] + dx * frame[nextX, y]) +
            dy * ((1 - dx) * frame[x, nextY] + dx * frame[nextX, nextY]);
    }
}
