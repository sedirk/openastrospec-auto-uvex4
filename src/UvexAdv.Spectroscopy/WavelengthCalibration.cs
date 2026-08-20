namespace UvexAdv.Spectroscopy;

public sealed record WavelengthPoint(double Pixel, double WavelengthNm);

public sealed record WavelengthSolution(double[] Coefficients, double RmsNm)
{
    public double PixelToWavelengthNm(double pixel)
    {
        var result = 0d;
        for (var power = 0; power < Coefficients.Length; power++)
        {
            result += Coefficients[power] * Math.Pow(pixel, power);
        }

        return result;
    }
}

public static class WavelengthCalibrator
{
    public static WavelengthSolution Fit(IReadOnlyList<WavelengthPoint> points, int degree = 2)
    {
        if (degree == 2 && points.Count < 5)
        {
            degree = 1;
        }

        if (points.Count < degree + 2)
        {
            throw new ArgumentException("Wavelength calibration needs at least three matched lines.", nameof(points));
        }

        var pixels = points.Select(static point => point.Pixel).ToArray();
        var wavelengths = points.Select(static point => point.WavelengthNm).ToArray();
        var coefficients = LeastSquares.Polynomial(pixels, wavelengths, degree);
        var residuals = points.Select(point => point.WavelengthNm - Evaluate(coefficients, point.Pixel)).ToArray();
        var rms = Math.Sqrt(residuals.Average(static residual => residual * residual));
        return new WavelengthSolution(coefficients, rms);
    }

    private static double Evaluate(IReadOnlyList<double> coefficients, double pixel) =>
        coefficients.Select((coefficient, power) => coefficient * Math.Pow(pixel, power)).Sum();
}

public sealed record WavelengthCorrection(
    double PixelError,
    int CorrectionSteps,
    bool WithinTolerance,
    bool IsValid,
    string? FailureReason = null);

public static class WavelengthLock
{
    public static WavelengthCorrection Calculate(
        SpectralLineMeasurement line,
        double targetPixel,
        double gratingStepsPerPixel,
        double gain = 0.7,
        double tolerancePixels = 0.25,
        int maximumCorrectionSteps = 500)
    {
        if (!line.IsValid || !double.IsFinite(line.CentroidPixel))
        {
            return new WavelengthCorrection(double.NaN, 0, false, false, line.FailureReason ?? "Reference line is invalid.");
        }

        if (!double.IsFinite(gratingStepsPerPixel) || gratingStepsPerPixel == 0 || gain is <= 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(gratingStepsPerPixel));
        }

        var error = targetPixel - line.CentroidPixel;
        if (Math.Abs(error) <= tolerancePixels)
        {
            return new WavelengthCorrection(error, 0, true, true);
        }

        var raw = error * gratingStepsPerPixel * gain;
        var correction = (int)Math.Round(Math.Clamp(raw, -maximumCorrectionSteps, maximumCorrectionSteps), MidpointRounding.AwayFromZero);
        return new WavelengthCorrection(error, correction, false, correction != 0, correction == 0 ? "Correction rounded to zero before reaching tolerance." : null);
    }
}
