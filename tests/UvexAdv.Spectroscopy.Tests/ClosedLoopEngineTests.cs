using UvexAdv.Spectroscopy;

namespace UvexAdv.Spectroscopy.Tests;

public sealed class ClosedLoopEngineTests
{
    [Fact]
    public async Task AutofocusConvergesAndVerifies()
    {
        var context = new SyntheticFocusContext(initialPosition: 0, optimum: 100);
        var result = await SpectralAutofocusEngine.RunAsync(
            context,
            new AutofocusOptions(100, 7, -1000, 1000, [new(50), new(100), new(150)]),
            CancellationToken.None);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.InRange(result.FinalPositionSteps, 95, 105);
        Assert.Equal(7, result.Samples.Count);
    }

    [Fact]
    public async Task WavelengthLockRequiresTwoFramesInTolerance()
    {
        var context = new SyntheticWavelengthContext(initialCentroid: 95, stepsPerPixel: 10);
        var result = await WavelengthLockEngine.RunAsync(
            context,
            new WavelengthLockOptions(new SpectralLineWindow(100, 15), 100, 10),
            CancellationToken.None);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.True(result.Iterations.Count >= 2);
        Assert.True(result.Iterations[^1].Correction.WithinTolerance);
    }

    [Fact]
    public async Task WavelengthLockRollsBackWhenReferenceLineIsMissing()
    {
        var context = new SyntheticWavelengthContext(initialCentroid: double.NaN, stepsPerPixel: 10);
        var result = await WavelengthLockEngine.RunAsync(
            context,
            new WavelengthLockOptions(new SpectralLineWindow(100, 15), 100, 10),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(0, context.CurrentGratingPositionSteps);
    }

    private sealed class SyntheticFocusContext(int initialPosition, int optimum) : ISpectralFocusContext
    {
        public int CurrentFocusPositionSteps { get; private set; } = initialPosition;

        public Task MoveFocusToAsync(int positionSteps, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CurrentFocusPositionSteps = positionSteps;
            return Task.CompletedTask;
        }

        public Task<Spectrum1D> CaptureSpectrumAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sigma = 1.2 + (0.00001 * Math.Pow(CurrentFocusPositionSteps - optimum, 2));
            return Task.FromResult(BuildSpectrum([50, 100, 150], sigma));
        }
    }

    private sealed class SyntheticWavelengthContext(double initialCentroid, double stepsPerPixel) : IWavelengthLockContext
    {
        public int CurrentGratingPositionSteps { get; private set; }

        public Task MoveGratingRelativeAsync(int deltaSteps, CancellationToken cancellationToken)
        {
            CurrentGratingPositionSteps += deltaSteps;
            return Task.CompletedTask;
        }

        public Task<Spectrum1D> CaptureSpectrumAsync(CancellationToken cancellationToken)
        {
            if (!double.IsFinite(initialCentroid))
            {
                return Task.FromResult(new Spectrum1D(new double[200], 0, new ImageRoi(0, 0, 200, 1), DispersionAxis.Horizontal));
            }

            var centroid = initialCentroid + (CurrentGratingPositionSteps / stepsPerPixel);
            return Task.FromResult(BuildSpectrum([centroid], 1.5));
        }
    }

    private static Spectrum1D BuildSpectrum(IEnumerable<double> centers, double sigma)
    {
        var flux = new double[200];
        for (var x = 0; x < flux.Length; x++)
        {
            flux[x] = centers.Sum(center => 5000 * Math.Exp(-0.5 * Math.Pow((x - center) / sigma, 2)));
        }

        return new Spectrum1D(flux, 0, new ImageRoi(0, 0, flux.Length, 1), DispersionAxis.Horizontal);
    }
}
