namespace UvexAdv.Observatory;

public sealed record MountCalibrationSample(
    double CommandedRaArcseconds,
    double CommandedDecArcseconds,
    double MeasuredPixelShiftX,
    double MeasuredPixelShiftY);

public sealed record MountTransformCalibration(
    GateResult Gate,
    PixelToMountTransform? Transform,
    double Determinant,
    double ConditionEstimate);

public static class MountTransformCalibrator
{
    public static MountTransformCalibration Fit(
        string calibrationId,
        string pierSide,
        IReadOnlyList<MountCalibrationSample> samples,
        double maximumResidualArcseconds = 1.5,
        double maximumConditionEstimate = 20)
    {
        if (samples.Count < 2)
        {
            return new MountTransformCalibration(GateResult.Fail("TRANSFORM_SAMPLES_MISSING", "At least two independent bounded mount calibration samples are required."), null, 0, double.PositiveInfinity);
        }

        // Least-squares fit from pixel shift to commanded sky correction: sky = P * coefficient.
        double sxx = 0, sxy = 0, syy = 0;
        double sxRa = 0, syRa = 0, sxDec = 0, syDec = 0;
        foreach (var sample in samples)
        {
            var x = sample.MeasuredPixelShiftX;
            var y = sample.MeasuredPixelShiftY;
            sxx += x * x;
            sxy += x * y;
            syy += y * y;
            sxRa += x * sample.CommandedRaArcseconds;
            syRa += y * sample.CommandedRaArcseconds;
            sxDec += x * sample.CommandedDecArcseconds;
            syDec += y * sample.CommandedDecArcseconds;
        }
        var determinant = sxx * syy - sxy * sxy;
        if (Math.Abs(determinant) < 1e-9)
        {
            return new MountTransformCalibration(GateResult.Fail("TRANSFORM_SINGULAR", "Mount calibration motions did not span two independent image directions."), null, determinant, double.PositiveInfinity);
        }
        var inv00 = syy / determinant;
        var inv01 = -sxy / determinant;
        var inv11 = sxx / determinant;
        var raX = inv00 * sxRa + inv01 * syRa;
        var raY = inv01 * sxRa + inv11 * syRa;
        var decX = inv00 * sxDec + inv01 * syDec;
        var decY = inv01 * sxDec + inv11 * syDec;
        var trace = sxx + syy;
        var discriminant = Math.Sqrt(Math.Max(0, trace * trace - 4 * determinant));
        var largest = (trace + discriminant) / 2;
        var smallest = (trace - discriminant) / 2;
        var condition = smallest > 0 ? Math.Sqrt(largest / smallest) : double.PositiveInfinity;

        var residualSquares = 0d;
        foreach (var sample in samples)
        {
            var predictedRa = raX * sample.MeasuredPixelShiftX + raY * sample.MeasuredPixelShiftY;
            var predictedDec = decX * sample.MeasuredPixelShiftX + decY * sample.MeasuredPixelShiftY;
            var dra = predictedRa - sample.CommandedRaArcseconds;
            var ddec = predictedDec - sample.CommandedDecArcseconds;
            residualSquares += dra * dra + ddec * ddec;
        }
        var rms = Math.Sqrt(residualSquares / samples.Count);
        var transform = new PixelToMountTransform(calibrationId, raX, raY, decX, decY, pierSide, rms, DateTimeOffset.UtcNow);
        var metrics = new Dictionary<string, double>
        {
            ["transformRmsArcseconds"] = rms,
            ["conditionEstimate"] = condition,
            ["normalMatrixDeterminant"] = determinant
        };
        if (condition > maximumConditionEstimate)
        {
            return new MountTransformCalibration(GateResult.Fail("TRANSFORM_ILL_CONDITIONED", $"Mount/image transform condition estimate {condition:F2} exceeds {maximumConditionEstimate:F2}.", metrics), transform, determinant, condition);
        }
        if (rms > maximumResidualArcseconds)
        {
            return new MountTransformCalibration(GateResult.Fail("TRANSFORM_RESIDUAL_HIGH", $"Mount/image transform RMS {rms:F2} arcsec exceeds {maximumResidualArcseconds:F2} arcsec.", metrics), transform, determinant, condition);
        }
        return new MountTransformCalibration(GateResult.Pass("TRANSFORM_CALIBRATED", $"Mount/image transform fitted with {rms:F2} arcsec RMS.", metrics), transform, determinant, condition);
    }
}

public sealed record SlitThroughputSample(
    double CrossSlitOffsetArcseconds,
    double BackgroundSubtractedFlux,
    double FluxUncertainty,
    double SaturatedFraction,
    string FrameId);

public sealed record SlitThroughputSolution(
    GateResult Gate,
    double BestOffsetArcseconds,
    double PredictedPeakFlux,
    double Curvature,
    IReadOnlyList<SlitThroughputSample> Samples);

public static class SlitThroughputOptimizer
{
    public static SlitThroughputSolution Fit(
        IReadOnlyList<SlitThroughputSample> samples,
        double maximumAbsoluteOffsetArcseconds,
        double maximumSaturatedFraction = 0.001)
    {
        var valid = samples
            .Where(sample => double.IsFinite(sample.CrossSlitOffsetArcseconds)
                             && double.IsFinite(sample.BackgroundSubtractedFlux)
                             && sample.BackgroundSubtractedFlux > 0
                             && sample.FluxUncertainty > 0
                             && sample.SaturatedFraction <= maximumSaturatedFraction)
            .ToArray();
        if (valid.Length < 3)
        {
            return new SlitThroughputSolution(GateResult.Fail("SLIT_SCAN_INSUFFICIENT", "Fewer than three unsaturated slit-throughput samples are valid."), 0, double.NaN, double.NaN, samples);
        }

        // Weighted normal equations for y = a*x^2 + b*x + c.
        var matrix = new double[3, 4];
        foreach (var sample in valid)
        {
            var x = sample.CrossSlitOffsetArcseconds;
            var basis = new[] { x * x, x, 1d };
            var weight = 1 / (sample.FluxUncertainty * sample.FluxUncertainty);
            for (var row = 0; row < 3; row++)
            {
                for (var column = 0; column < 3; column++) matrix[row, column] += weight * basis[row] * basis[column];
                matrix[row, 3] += weight * basis[row] * sample.BackgroundSubtractedFlux;
            }
        }
        if (!Solve3x3(matrix, out var coefficients))
        {
            return new SlitThroughputSolution(GateResult.Fail("SLIT_SCAN_SINGULAR", "Slit scan positions do not support a stable quadratic fit."), 0, double.NaN, double.NaN, samples);
        }
        var a = coefficients[0];
        var b = coefficients[1];
        var c = coefficients[2];
        if (a >= 0)
        {
            var measuredBest = valid.MaxBy(sample => sample.BackgroundSubtractedFlux)!;
            return new SlitThroughputSolution(GateResult.Unknown("SLIT_SCAN_NOT_PEAKED", "Slit throughput scan does not contain a concave peak; a wider bounded scan is required."), measuredBest.CrossSlitOffsetArcseconds, measuredBest.BackgroundSubtractedFlux, a, samples);
        }
        var optimum = -b / (2 * a);
        var peak = a * optimum * optimum + b * optimum + c;
        var metrics = new Dictionary<string, double>
        {
            ["bestCrossSlitOffsetArcseconds"] = optimum,
            ["predictedPeakFlux"] = peak,
            ["quadraticCurvature"] = a
        };
        if (!double.IsFinite(optimum) || Math.Abs(optimum) > maximumAbsoluteOffsetArcseconds)
        {
            return new SlitThroughputSolution(GateResult.Fail("SLIT_SCAN_OUT_OF_RANGE", $"Predicted slit optimum {optimum:F2} arcsec lies outside the bounded scan range ±{maximumAbsoluteOffsetArcseconds:F2} arcsec.", metrics), optimum, peak, a, samples);
        }
        return new SlitThroughputSolution(GateResult.Pass("SLIT_THROUGHPUT_PEAK", $"Slit throughput peak is predicted at {optimum:+0.00;-0.00;0.00} arcsec.", metrics), optimum, peak, a, samples);
    }

    private static bool Solve3x3(double[,] augmented, out double[] result)
    {
        result = new double[3];
        for (var pivot = 0; pivot < 3; pivot++)
        {
            var best = pivot;
            for (var row = pivot + 1; row < 3; row++) if (Math.Abs(augmented[row, pivot]) > Math.Abs(augmented[best, pivot])) best = row;
            if (Math.Abs(augmented[best, pivot]) < 1e-12) return false;
            if (best != pivot)
            {
                for (var column = pivot; column < 4; column++) (augmented[pivot, column], augmented[best, column]) = (augmented[best, column], augmented[pivot, column]);
            }
            var divisor = augmented[pivot, pivot];
            for (var column = pivot; column < 4; column++) augmented[pivot, column] /= divisor;
            for (var row = 0; row < 3; row++)
            {
                if (row == pivot) continue;
                var factor = augmented[row, pivot];
                for (var column = pivot; column < 4; column++) augmented[row, column] -= factor * augmented[pivot, column];
            }
        }
        for (var row = 0; row < 3; row++) result[row] = augmented[row, 3];
        return true;
    }
}
