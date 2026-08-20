using System.Globalization;
using System.Text;

namespace UvexAdv.Core;

public enum CalibrationFrameKind
{
    Bias,
    Dark,
}

public sealed record CalibrationCaptureGroup(
    CalibrationFrameKind Kind,
    double ExposureSeconds,
    int FrameCount)
{
    public string ExposureKey => CalibrationLibraryPath.ExposureKey(ExposureSeconds);
}

public sealed record CalibrationCapturePlan(
    string CameraName,
    string CameraId,
    int Gain,
    int Offset,
    short Binning,
    short ReadoutModeIndex,
    string ReadoutModeName,
    double TemperatureC,
    double TemperatureToleranceC,
    int WarmupFrameCount,
    IReadOnlyList<CalibrationCaptureGroup> Groups)
{
    public static CalibrationCapturePlan Create(
        string cameraName,
        string cameraId,
        int gain,
        int offset,
        short binning,
        short readoutModeIndex,
        string readoutModeName,
        double temperatureC,
        double temperatureToleranceC,
        int warmupFrameCount,
        double biasExposureSeconds,
        int biasFrameCount,
        IEnumerable<double> darkExposureSeconds,
        int darkFrameCount)
    {
        if (string.IsNullOrWhiteSpace(cameraName)) throw new ArgumentException("Camera name is required.", nameof(cameraName));
        if (string.IsNullOrWhiteSpace(cameraId)) throw new ArgumentException("A stable camera DeviceId is required.", nameof(cameraId));
        if (gain < 0) throw new ArgumentOutOfRangeException(nameof(gain));
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (binning is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(binning));
        if (readoutModeIndex < 0) throw new ArgumentOutOfRangeException(nameof(readoutModeIndex));
        if (string.IsNullOrWhiteSpace(readoutModeName)) throw new ArgumentException("Readout mode name is required.", nameof(readoutModeName));
        if (!double.IsFinite(temperatureC) || temperatureC is < -60 or > 40) throw new ArgumentOutOfRangeException(nameof(temperatureC));
        if (!double.IsFinite(temperatureToleranceC) || temperatureToleranceC is <= 0 or > 10) throw new ArgumentOutOfRangeException(nameof(temperatureToleranceC));
        if (warmupFrameCount is < 0 or > 20) throw new ArgumentOutOfRangeException(nameof(warmupFrameCount));
        if (!double.IsFinite(biasExposureSeconds) || biasExposureSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(biasExposureSeconds));
        if (biasFrameCount < 0) throw new ArgumentOutOfRangeException(nameof(biasFrameCount));
        if (darkFrameCount < 0) throw new ArgumentOutOfRangeException(nameof(darkFrameCount));

        var groups = new List<CalibrationCaptureGroup>();
        if (biasFrameCount > 0)
        {
            groups.Add(new CalibrationCaptureGroup(CalibrationFrameKind.Bias, biasExposureSeconds, biasFrameCount));
        }

        foreach (var exposure in darkExposureSeconds.Distinct().Order())
        {
            if (!double.IsFinite(exposure) || exposure <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(darkExposureSeconds), "Dark exposures must be finite positive values.");
            }

            if (darkFrameCount > 0)
            {
                groups.Add(new CalibrationCaptureGroup(CalibrationFrameKind.Dark, exposure, darkFrameCount));
            }
        }

        if (groups.Count == 0) throw new ArgumentException("The capture plan contains no frames.");

        return new CalibrationCapturePlan(
            cameraName.Trim(),
            cameraId.Trim(),
            gain,
            offset,
            binning,
            readoutModeIndex,
            readoutModeName.Trim(),
            temperatureC,
            temperatureToleranceC,
            warmupFrameCount,
            groups);
    }
}

public static class CalibrationLibraryPath
{
    public static string GetConfigurationDirectory(string root, CalibrationCapturePlan plan)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("Library root is required.", nameof(root));
        return Path.Combine(
            Path.GetFullPath(Environment.ExpandEnvironmentVariables(root.Trim())),
            SafeSegment(plan.CameraName),
            $"G{plan.Gain}_O{plan.Offset}",
            $"B{plan.Binning}x{plan.Binning}",
            $"R{plan.ReadoutModeIndex}_{SafeSegment(plan.ReadoutModeName)}",
            $"T{SignedNumber(plan.TemperatureC)}C");
    }

    public static string GetRawDirectory(string root, CalibrationCapturePlan plan, CalibrationCaptureGroup group, DateOnly date)
    {
        var typeDirectory = group.Kind == CalibrationFrameKind.Bias
            ? "BIAS"
            : Path.Combine("DARK", group.ExposureKey);
        return Path.Combine(GetConfigurationDirectory(root, plan), typeDirectory, "raw", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
    }

    public static string GetMasterDirectory(string root, CalibrationCapturePlan plan, CalibrationCaptureGroup group)
    {
        var typeDirectory = group.Kind == CalibrationFrameKind.Bias
            ? "BIAS"
            : Path.Combine("DARK", group.ExposureKey);
        return Path.Combine(GetConfigurationDirectory(root, plan), typeDirectory, "masters");
    }

    public static string ExposureKey(double exposureSeconds)
    {
        if (exposureSeconds >= 1)
        {
            return $"{exposureSeconds.ToString("0.###", CultureInfo.InvariantCulture)}s";
        }

        return $"{(exposureSeconds * 1000).ToString("0.###", CultureInfo.InvariantCulture)}ms";
    }

    public static string SafeSegment(string value)
    {
        var builder = new StringBuilder(value.Length);
        var previousWasSeparator = false;
        foreach (var character in value.Trim())
        {
            if (char.IsLetterOrDigit(character) || character is '-' or '_')
            {
                builder.Append(character);
                previousWasSeparator = false;
            }
            else if (!previousWasSeparator)
            {
                builder.Append('_');
                previousWasSeparator = true;
            }
        }

        var result = builder.ToString().Trim('_');
        return string.IsNullOrWhiteSpace(result) ? "UnknownCamera" : result;
    }

    private static string SignedNumber(double value)
    {
        var prefix = value >= 0 ? "+" : "-";
        return prefix + Math.Abs(value).ToString("0.#", CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// Streaming pixel accumulator. Five or more frames use a one-high/one-low trimmed mean;
/// smaller groups use an ordinary mean and should be treated as provisional masters.
/// </summary>
public sealed class RobustFrameAccumulator
{
    private readonly double[] sums;
    private readonly ushort[] minima;
    private readonly ushort[] maxima;

    public RobustFrameAccumulator(int pixelCount)
    {
        if (pixelCount <= 0) throw new ArgumentOutOfRangeException(nameof(pixelCount));
        sums = new double[pixelCount];
        minima = new ushort[pixelCount];
        maxima = new ushort[pixelCount];
    }

    public int PixelCount => sums.Length;
    public int FrameCount { get; private set; }
    public bool UsesTrimmedMean => FrameCount >= 5;

    public void Add(ReadOnlySpan<ushort> pixels)
    {
        if (pixels.Length != PixelCount) throw new ArgumentException("Frame dimensions changed within a calibration group.", nameof(pixels));

        if (FrameCount == 0)
        {
            for (var index = 0; index < pixels.Length; index++)
            {
                var value = pixels[index];
                sums[index] = value;
                minima[index] = value;
                maxima[index] = value;
            }
        }
        else
        {
            for (var index = 0; index < pixels.Length; index++)
            {
                var value = pixels[index];
                sums[index] += value;
                if (value < minima[index]) minima[index] = value;
                if (value > maxima[index]) maxima[index] = value;
            }
        }

        FrameCount++;
    }

    public ushort[] BuildMaster()
    {
        if (FrameCount == 0) throw new InvalidOperationException("No calibration frames have been accumulated.");

        var output = new ushort[PixelCount];
        var trim = UsesTrimmedMean;
        var denominator = trim ? FrameCount - 2 : FrameCount;
        for (var index = 0; index < output.Length; index++)
        {
            var total = trim ? sums[index] - minima[index] - maxima[index] : sums[index];
            output[index] = (ushort)Math.Clamp(Math.Round(total / denominator), ushort.MinValue, ushort.MaxValue);
        }

        return output;
    }
}
