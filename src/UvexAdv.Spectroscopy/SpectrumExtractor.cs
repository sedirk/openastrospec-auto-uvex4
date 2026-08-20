namespace UvexAdv.Spectroscopy;

public sealed record SpectrumExtractionOptions(
    ImageRoi Roi,
    DispersionAxis Axis = DispersionAxis.Horizontal,
    int ApertureStart = 0,
    int ApertureLength = 0,
    double CosmicRaySigma = 6,
    double SaturationFractionLimit = 0.005);

public static class SpectrumExtractor
{
    public static Spectrum1D Extract(SpectralImage image, SpectrumExtractionOptions options)
    {
        options.Roi.Validate(image.Width, image.Height);
        var crossLength = options.Axis == DispersionAxis.Horizontal ? options.Roi.Height : options.Roi.Width;
        var dispersionLength = options.Axis == DispersionAxis.Horizontal ? options.Roi.Width : options.Roi.Height;
        var apertureStart = options.ApertureStart;
        var apertureLength = options.ApertureLength <= 0 ? crossLength : options.ApertureLength;
        if (apertureStart < 0 || apertureLength < 1 || apertureStart + apertureLength > crossLength)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Extraction aperture is outside the ROI.");
        }

        var flux = new double[dispersionLength];
        var saturated = 0;
        var totalAperturePixels = checked(dispersionLength * apertureLength);

        for (var d = 0; d < dispersionLength; d++)
        {
            var aperture = new double[apertureLength];
            var background = new List<double>(crossLength - apertureLength);
            for (var c = 0; c < crossLength; c++)
            {
                var pixel = GetPixel(image, options.Roi, options.Axis, d, c);
                if (c >= apertureStart && c < apertureStart + apertureLength)
                {
                    var index = c - apertureStart;
                    aperture[index] = pixel;
                    if (pixel >= image.SaturationLevel)
                    {
                        saturated++;
                    }
                }
                else
                {
                    background.Add(pixel);
                }
            }

            var backgroundLevel = background.Count > 0 ? RobustStatistics.Median(background) : 0;
            var adjusted = aperture.Select(value => value - backgroundLevel).ToArray();
            var median = RobustStatistics.Median(adjusted);
            var mad = RobustStatistics.MedianAbsoluteDeviation(adjusted, median);
            var sigma = Math.Max(1e-9, mad * 1.4826);
            var upper = median + (options.CosmicRaySigma * sigma);
            flux[d] = adjusted.Sum(value => Math.Min(value, upper));
        }

        var saturatedFraction = saturated / (double)totalAperturePixels;
        if (saturatedFraction > options.SaturationFractionLimit)
        {
            throw new InvalidOperationException($"Saturated pixel fraction {saturatedFraction:P3} exceeds the configured limit.");
        }

        return new Spectrum1D(flux, saturatedFraction, options.Roi, options.Axis);
    }

    private static double GetPixel(SpectralImage image, ImageRoi roi, DispersionAxis axis, int dispersion, int cross) =>
        axis == DispersionAxis.Horizontal
            ? image[roi.X + dispersion, roi.Y + cross]
            : image[roi.X + cross, roi.Y + dispersion];
}
