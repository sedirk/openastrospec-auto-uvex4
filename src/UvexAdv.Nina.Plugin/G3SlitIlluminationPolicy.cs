using UvexAdv.Observatory;
using System.Runtime.ExceptionServices;

namespace UvexAdv.Nina.Plugin;

/// <summary>
/// Fixed fail-closed policy for the real G3 slit-illumination sequence. These
/// are analysis/capture-quality limits, not mechanical commissioning values.
/// The historical slit geometry remains only the bounded analyzer seed.
/// </summary>
internal static class G3SlitIlluminationPolicy
{
    public const int FramesPerPhase = 3;
    // PHD2 expresses camera gain as 0..100 percent. For the commissioned
    // G3M2210M, 0% maps to the native minimum (100), while the guiding profile
    // remains at 100% (native 15000). LED geometry needs dynamic range, not
    // guide-star sensitivity, so every native single frame uses the minimum.
    public const int CaptureGainPercent = 0;
    public const double MinimumAcceptedConfidence = 0.50;

    /// <summary>
    /// Produces a robust, unregistered per-pixel median. The real sequence uses
    /// three frames for each lamp phase, so one transition/outlier frame cannot
    /// by itself define the differential slit. The six before/after OFF frames
    /// are combined by the same median rule.
    /// </summary>
    public static MonochromeFrame MedianComposite(IReadOnlyList<MonochromeFrame> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);
        if (frames.Count < 2)
        {
            throw new ArgumentException("A robust G3 composite requires at least two frames.", nameof(frames));
        }

        var first = frames[0] ?? throw new ArgumentException("G3 composite frame is null.", nameof(frames));
        if (frames.Any(frame => frame is null || frame.Width != first.Width || frame.Height != first.Height))
        {
            throw new ArgumentException("All G3 composite frames must have identical dimensions.", nameof(frames));
        }

        var saturation = frames.Min(frame => frame.SaturationLevel);
        var pixels = new ushort[checked(first.Width * first.Height)];
        var samples = new ushort[frames.Count];
        for (var index = 0; index < pixels.Length; index++)
        {
            var x = index % first.Width;
            var y = index / first.Width;
            for (var frameIndex = 0; frameIndex < frames.Count; frameIndex++)
            {
                samples[frameIndex] = frames[frameIndex][x, y];
            }
            Array.Sort(samples);
            var center = samples.Length / 2;
            pixels[index] = samples.Length % 2 == 1
                ? samples[center]
                : (ushort)(((uint)samples[center - 1] + samples[center] + 1) / 2);
        }

        return new MonochromeFrame(first.Width, first.Height, pixels, saturation);
    }

    public static SlitIlluminationPairAnalysis ApplyConfidenceGate(
        SlitIlluminationPairAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        if (analysis.Gate.Disposition != GateDisposition.Passed ||
            analysis.Confidence >= MinimumAcceptedConfidence)
        {
            return analysis;
        }

        var metrics = analysis.Gate.Metrics is null
            ? new Dictionary<string, double>()
            : new Dictionary<string, double>(analysis.Gate.Metrics);
        metrics["confidence"] = analysis.Confidence;
        metrics["minimumAcceptedConfidence"] = MinimumAcceptedConfidence;
        return analysis with
        {
            Gate = GateResult.Unknown(
                "SLIT_LED_PAIR_LOW_CONFIDENCE",
                $"Paired slit illumination confidence {analysis.Confidence:F3} is below the automatic-placement threshold {MinimumAcceptedConfidence:F3}; operator attention is required.",
                metrics),
        };
    }
}

/// <summary>
/// Runs the short LED-ON capture block without a cooperative pause checkpoint,
/// then proves OFF before entering the checkpoint that may wait for Resume.
/// Cancellation and hard timeout still interrupt capture and execute OFF with
/// an independent bounded token before the original failure is rethrown.
/// </summary>
internal static class G3AtomicLedOnBlock
{
    public static async Task ExecuteAsync(
        int frameCount,
        TimeSpan maximumOnDuration,
        TimeSpan offTimeout,
        Func<CancellationToken, Task> turnOn,
        Func<int, CancellationToken, Task> captureFrame,
        Func<CancellationToken, Task> turnOff,
        Func<CancellationToken, Task> checkpointAfterOff,
        CancellationToken cancellationToken)
    {
        if (frameCount < 1) throw new ArgumentOutOfRangeException(nameof(frameCount));
        if (maximumOnDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(maximumOnDuration));
        if (offTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(offTimeout));
        ArgumentNullException.ThrowIfNull(turnOn);
        ArgumentNullException.ThrowIfNull(captureFrame);
        ArgumentNullException.ThrowIfNull(turnOff);
        ArgumentNullException.ThrowIfNull(checkpointAfterOff);

        Exception? captureFailure = null;
        using var atomicTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        atomicTimeout.CancelAfter(maximumOnDuration);
        try
        {
            await turnOn(atomicTimeout.Token).ConfigureAwait(false);
            for (var index = 1; index <= frameCount; index++)
            {
                atomicTimeout.Token.ThrowIfCancellationRequested();
                await captureFrame(index, atomicTimeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException ex) when (
            atomicTimeout.IsCancellationRequested &&
            !cancellationToken.IsCancellationRequested)
        {
            captureFailure = new TimeoutException(
                $"The bounded {frameCount}-frame G3 LED-ON block exceeded {maximumOnDuration}.",
                ex);
        }
        catch (Exception ex)
        {
            captureFailure = ex;
        }

        try
        {
            using var offDeadline = new CancellationTokenSource(offTimeout);
            await turnOff(offDeadline.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new SlitIlluminationSafetyException(
                $"The bounded G3 LED-ON block could not command/readback-verify OFF: {ex.Message}",
                captureFailure ?? ex);
        }

        if (captureFailure is not null)
        {
            ExceptionDispatchInfo.Capture(captureFailure).Throw();
            throw new InvalidOperationException("Unreachable after rethrowing the LED-ON capture failure.");
        }

        // A Pause requested after ON-1 first reaches its wait here, after OFF.
        await checkpointAfterOff(cancellationToken).ConfigureAwait(false);
    }
}
