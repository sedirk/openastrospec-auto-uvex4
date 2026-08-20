namespace UvexAdv.Spectroscopy;

public interface ISpectralFocusContext
{
    int CurrentFocusPositionSteps { get; }
    Task MoveFocusToAsync(int positionSteps, CancellationToken cancellationToken);
    Task<Spectrum1D> CaptureSpectrumAsync(CancellationToken cancellationToken);
}

public sealed record AutofocusOptions(
    int StepSize,
    int SampleCount,
    int MinimumPosition,
    int MaximumPosition,
    IReadOnlyList<SpectralLineWindow> Lines,
    int BacklashSteps = 0,
    int VerificationFrames = 3,
    double MaximumVerificationDegradation = 0.02);

public sealed record AutofocusResult(
    bool Succeeded,
    int InitialPositionSteps,
    int FinalPositionSteps,
    FocusFit? Fit,
    FocusMetric? VerificationMetric,
    IReadOnlyList<FocusSample> Samples,
    string? FailureReason = null);

public static class SpectralAutofocusEngine
{
    public static async Task<AutofocusResult> RunAsync(
        ISpectralFocusContext context,
        AutofocusOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (options.Lines.Count < 3)
        {
            throw new ArgumentException("At least three focus lines must be configured.", nameof(options));
        }

        var initialPosition = context.CurrentFocusPositionSteps;
        var initialMetric = FocusMetricCalculator.Calculate(
            await context.CaptureSpectrumAsync(cancellationToken).ConfigureAwait(false),
            options.Lines);
        var samples = new List<FocusSample>();
        FocusFit? fit = null;

        try
        {
            var positions = FocusSamplingPlan.Symmetric(initialPosition, options.StepSize, options.SampleCount);
            if (positions.Any(position => position < options.MinimumPosition || position > options.MaximumPosition))
            {
                throw new InvalidOperationException("Autofocus sampling plan exceeds the configured M2 travel limits.");
            }

            await ApproachFromBelowAsync(context, positions[0], options.BacklashSteps, options.MinimumPosition, cancellationToken).ConfigureAwait(false);
            foreach (var position in positions)
            {
                await context.MoveFocusToAsync(position, cancellationToken).ConfigureAwait(false);
                var spectrum = await context.CaptureSpectrumAsync(cancellationToken).ConfigureAwait(false);
                samples.Add(new FocusSample(position, FocusMetricCalculator.Calculate(spectrum, options.Lines)));
            }

            fit = FocusCurveFitter.Fit(samples);
            if (!fit.IsValid)
            {
                throw new InvalidOperationException(fit.FailureReason);
            }

            var optimum = (int)Math.Round(fit.OptimumPositionSteps, MidpointRounding.AwayFromZero);
            if (optimum < options.MinimumPosition || optimum > options.MaximumPosition)
            {
                throw new InvalidOperationException("Autofocus optimum exceeds the configured M2 travel limits.");
            }

            await ApproachFromBelowAsync(context, optimum, options.BacklashSteps, options.MinimumPosition, cancellationToken).ConfigureAwait(false);
            var verification = new List<FocusMetric>();
            for (var frame = 0; frame < Math.Max(1, options.VerificationFrames); frame++)
            {
                verification.Add(FocusMetricCalculator.Calculate(
                    await context.CaptureSpectrumAsync(cancellationToken).ConfigureAwait(false),
                    options.Lines));
            }

            var validVerification = verification.Where(metric => double.IsFinite(metric.FwhmPixels)).ToArray();
            if (validVerification.Length != verification.Count)
            {
                throw new InvalidOperationException("One or more verification frames did not contain enough valid spectral lines.");
            }

            var finalMetric = validVerification.OrderBy(metric => metric.FwhmPixels).ElementAt(validVerification.Length / 2);
            if (double.IsFinite(initialMetric.FwhmPixels) &&
                finalMetric.FwhmPixels > initialMetric.FwhmPixels * (1 + options.MaximumVerificationDegradation))
            {
                throw new InvalidOperationException("Verification FWHM is more than 2% worse than the initial focus metric.");
            }

            return new AutofocusResult(true, initialPosition, optimum, fit, finalMetric, samples);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await context.MoveFocusToAsync(initialPosition, CancellationToken.None).ConfigureAwait(false);
            return new AutofocusResult(false, initialPosition, initialPosition, fit, null, samples, ex.Message);
        }
        catch (OperationCanceledException)
        {
            await context.MoveFocusToAsync(initialPosition, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task ApproachFromBelowAsync(
        ISpectralFocusContext context,
        int target,
        int backlashSteps,
        int minimum,
        CancellationToken cancellationToken)
    {
        if (backlashSteps > 0)
        {
            await context.MoveFocusToAsync(Math.Max(minimum, target - backlashSteps), cancellationToken).ConfigureAwait(false);
        }

        await context.MoveFocusToAsync(target, cancellationToken).ConfigureAwait(false);
    }
}

public interface IWavelengthLockContext
{
    int CurrentGratingPositionSteps { get; }
    Task MoveGratingRelativeAsync(int deltaSteps, CancellationToken cancellationToken);
    Task<Spectrum1D> CaptureSpectrumAsync(CancellationToken cancellationToken);
}

public sealed record WavelengthLockOptions(
    SpectralLineWindow ReferenceLine,
    double TargetPixel,
    double GratingStepsPerPixel,
    double Gain = 0.7,
    double TolerancePixels = 0.25,
    int MaximumCorrectionSteps = 500,
    int MaximumIterations = 5,
    int RequiredConsecutiveFrames = 2);

public sealed record WavelengthLockIteration(
    int Iteration,
    int GratingPositionSteps,
    SpectralLineMeasurement Line,
    WavelengthCorrection Correction);

public sealed record WavelengthLockResult(
    bool Succeeded,
    int InitialPositionSteps,
    int FinalPositionSteps,
    IReadOnlyList<WavelengthLockIteration> Iterations,
    string? FailureReason = null);

public static class WavelengthLockEngine
{
    public static async Task<WavelengthLockResult> RunAsync(
        IWavelengthLockContext context,
        WavelengthLockOptions options,
        CancellationToken cancellationToken)
    {
        var initial = context.CurrentGratingPositionSteps;
        var iterations = new List<WavelengthLockIteration>();
        var consecutive = 0;
        try
        {
            for (var iteration = 1; iteration <= options.MaximumIterations; iteration++)
            {
                var spectrum = await context.CaptureSpectrumAsync(cancellationToken).ConfigureAwait(false);
                var line = SpectralLineMeasurer.Measure(spectrum, options.ReferenceLine);
                var correction = WavelengthLock.Calculate(
                    line,
                    options.TargetPixel,
                    options.GratingStepsPerPixel,
                    options.Gain,
                    options.TolerancePixels,
                    options.MaximumCorrectionSteps);
                iterations.Add(new WavelengthLockIteration(iteration, context.CurrentGratingPositionSteps, line, correction));
                if (!correction.IsValid)
                {
                    throw new InvalidOperationException(correction.FailureReason);
                }

                if (correction.WithinTolerance)
                {
                    consecutive++;
                    if (consecutive >= options.RequiredConsecutiveFrames)
                    {
                        return new WavelengthLockResult(true, initial, context.CurrentGratingPositionSteps, iterations);
                    }

                    continue;
                }

                consecutive = 0;
                await context.MoveGratingRelativeAsync(correction.CorrectionSteps, cancellationToken).ConfigureAwait(false);
            }

            throw new InvalidOperationException("Wavelength lock did not converge within the configured iteration limit.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await RollBackAsync(context, initial).ConfigureAwait(false);
            return new WavelengthLockResult(false, initial, initial, iterations, ex.Message);
        }
        catch (OperationCanceledException)
        {
            await RollBackAsync(context, initial).ConfigureAwait(false);
            throw;
        }
    }

    private static Task RollBackAsync(IWavelengthLockContext context, int initial) =>
        context.MoveGratingRelativeAsync(initial - context.CurrentGratingPositionSteps, CancellationToken.None);
}
