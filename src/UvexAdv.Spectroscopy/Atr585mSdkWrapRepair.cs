namespace UvexAdv.Spectroscopy;

public sealed record SdkWrapRepairResult(
    double SeamScoreSigma,
    bool Applied,
    int AppliedShiftPixels,
    string? FailureReason = null);

public static class Atr585mSdkWrapRepair
{
    public static SdkWrapRepairResult DetectAndRepairHorizontal(
        double[] pixels,
        int width,
        int height,
        ImageRoi roi,
        int apertureStart,
        int apertureLength,
        int shiftPixels = 64,
        double thresholdSigma = 4)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (width <= 0 || height <= 0 || pixels.Length != checked(width * height))
        {
            throw new ArgumentException("Image dimensions do not match the pixel buffer.", nameof(pixels));
        }

        roi.Validate(width, height);
        if (shiftPixels <= 0 || thresholdSigma <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(shiftPixels), "SDK wrap parameters must be positive.");
        }

        if (roi.X != 0 || roi.Width <= shiftPixels + 16)
        {
            return new SdkWrapRepairResult(
                double.NaN,
                false,
                0,
                "The horizontal ROI must include detector column zero and the documented SDK seam.");
        }

        var crossLength = roi.Height;
        var selectedLength = apertureLength <= 0 ? crossLength : apertureLength;
        if (apertureStart < 0 || selectedLength < 1 || apertureStart + selectedLength > crossLength)
        {
            return new SdkWrapRepairResult(double.NaN, false, 0, "The extraction aperture is outside the ROI.");
        }

        var yStart = roi.Y + apertureStart;
        var profile = new double[roi.Width];
        var samples = new double[selectedLength];
        for (var x = 0; x < roi.Width; x++)
        {
            var count = 0;
            for (var y = yStart; y < yStart + selectedLength; y++)
            {
                var value = pixels[(y * width) + x];
                if (double.IsFinite(value))
                {
                    samples[count++] = value;
                }
            }

            profile[x] = MedianInPlace(samples, count);
        }

        var smoothed = GaussianSmoothSigmaOne(profile);
        var differences = new double[smoothed.Length - 1];
        for (var index = 0; index < differences.Length; index++)
        {
            differences[index] = Math.Abs(smoothed[index + 1] - smoothed[index]);
        }

        var seamIndex = shiftPixels - 1;
        var high = Math.Min(differences.Length, Math.Max(160, 3 * shiftPixels));
        var baseline = Enumerable.Range(8, Math.Max(0, high - 8))
            .Where(index => Math.Abs(index - seamIndex) > 5 && double.IsFinite(differences[index]))
            .Select(index => differences[index])
            .ToArray();
        if (baseline.Length < 20 || seamIndex >= differences.Length || !double.IsFinite(differences[seamIndex]))
        {
            return new SdkWrapRepairResult(double.NaN, false, 0, "There are not enough valid samples to score the SDK seam.");
        }

        var center = RobustStatistics.Median(baseline);
        var scatter = 1.4826 * RobustStatistics.MedianAbsoluteDeviation(baseline, center);
        var scale = Math.Max(Math.Max(scatter, 0.1 * center), 1);
        var score = (differences[seamIndex] - center) / scale;
        if (!double.IsFinite(score) || score < thresholdSigma)
        {
            return new SdkWrapRepairResult(score, false, 0);
        }

        ApplyCyclicHorizontalShift(pixels, width, height, -shiftPixels);
        return new SdkWrapRepairResult(score, true, -shiftPixels);
    }

    private static void ApplyCyclicHorizontalShift(double[] pixels, int width, int height, int shift)
    {
        var normalized = ((shift % width) + width) % width;
        if (normalized == 0)
        {
            return;
        }

        var row = new double[width];
        for (var y = 0; y < height; y++)
        {
            var start = y * width;
            Array.Copy(pixels, start, row, 0, width);
            Array.Copy(row, width - normalized, pixels, start, normalized);
            Array.Copy(row, 0, pixels, start + normalized, width - normalized);
        }
    }

    private static double[] GaussianSmoothSigmaOne(double[] values)
    {
        double[] kernel = [
            0.004433048175243745,
            0.05400558262241449,
            0.2420362293761143,
            0.3990502796524549,
            0.2420362293761143,
            0.05400558262241449,
            0.004433048175243745,
        ];
        var result = new double[values.Length];
        for (var index = 0; index < values.Length; index++)
        {
            var sum = 0d;
            for (var offset = -3; offset <= 3; offset++)
            {
                var source = Math.Clamp(index + offset, 0, values.Length - 1);
                sum += values[source] * kernel[offset + 3];
            }

            result[index] = sum;
        }

        return result;
    }

    private static double MedianInPlace(double[] values, int count)
    {
        if (count == 0)
        {
            return double.NaN;
        }

        Array.Sort(values, 0, count);
        var middle = count / 2;
        return count % 2 == 0 ? (values[middle - 1] + values[middle]) / 2 : values[middle];
    }
}
