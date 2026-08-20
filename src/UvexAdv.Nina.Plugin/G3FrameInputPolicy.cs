using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

/// <summary>
/// Converts the PHD2 FITS container into an analysis frame using the locked
/// detector plateau, not the nominal FITS bit depth. G3M2210M currently emits
/// 12-bit-native samples in a 16-bit container, so CAMBPP/SATURATE alone cannot
/// establish the effective clipping level.
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

        return new MonochromeFrame(
            width,
            height,
            pixels,
            checked((ushort)configuration.SaturationAdu));
    }
}
