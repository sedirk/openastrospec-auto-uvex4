using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

/// <summary>
/// Converts the PHD2 FITS container into an analysis frame using the locked
/// detector plateau and the transport representation actually present in the
/// pixels. Some ToupTek SDK releases return native 12-bit samples (0..4095),
/// while newer releases left-align the same samples in a 16-bit container
/// (0, 16, 32, ... 65520). CAMBPP/SATURATE alone cannot distinguish those two
/// representations, because both have historically been written as 16-bit FITS.
/// </summary>
internal static class G3FrameInputPolicy
{
    public static MonochromeFrame Create(
        int width,
        int height,
        ReadOnlyMemory<ushort> pixels,
        G3RunConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.SaturationAdu is <= 0 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(configuration),
                "G3 saturation ADU must fit the unsigned 16-bit FITS container.");
        }

        var effectiveSaturationAdu = ResolveEffectiveSaturationAdu(
            pixels.Span,
            checked((ushort)configuration.SaturationAdu));

        return new MonochromeFrame(
            width,
            height,
            pixels,
            effectiveSaturationAdu);
    }

    internal static ushort ResolveEffectiveSaturationAdu(
        ReadOnlySpan<ushort> pixels,
        ushort configuredNativeSaturationAdu)
    {
        if (pixels.IsEmpty || configuredNativeSaturationAdu == ushort.MaxValue)
        {
            return configuredNativeSaturationAdu;
        }

        var maximum = 0;
        var nonZeroSamples = 0;
        Span<int> divisibleSamples = stackalloc int[4];
        ReadOnlySpan<int> candidateScales = [2, 4, 8, 16];

        foreach (var sample in pixels)
        {
            maximum = Math.Max(maximum, sample);
            if (sample == 0)
            {
                // Zero is divisible by every power of two and therefore carries
                // no information about the SDK transport representation.
                continue;
            }

            nonZeroSamples++;
            for (var index = 0; index < candidateScales.Length; index++)
            {
                if (sample % candidateScales[index] == 0)
                {
                    divisibleSamples[index]++;
                }
            }
        }

        // A lone hot/corrupt high pixel must not reinterpret an otherwise native
        // 12-bit frame. Real left-aligned frames have hundreds of non-zero
        // samples and near-universal power-of-two quantisation. Do not require
        // the image maximum to exceed the native plateau: a covered/LED-OFF
        // exposure can be dark while still using the same left-aligned SDK
        // transport representation as its LED-ON partner.
        if (nonZeroSamples < 256)
        {
            return configuredNativeSaturationAdu;
        }

        const double minimumQuantizedFraction = 0.999;
        for (var index = candidateScales.Length - 1; index >= 0; index--)
        {
            var scale = candidateScales[index];
            var scaledSaturation = configuredNativeSaturationAdu * (long)scale;
            if (scaledSaturation > ushort.MaxValue || maximum > scaledSaturation)
            {
                continue;
            }

            var quantizedFraction = divisibleSamples[index] / (double)nonZeroSamples;
            if (quantizedFraction >= minimumQuantizedFraction)
            {
                return checked((ushort)scaledSaturation);
            }
        }

        return configuredNativeSaturationAdu;
    }
}
