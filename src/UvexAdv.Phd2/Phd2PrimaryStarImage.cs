using System.Buffers.Binary;
using System.Text.Json;

namespace UvexAdv.Phd2;

/// <summary>
/// PHD2 get_star_image returns its PRIMARY centroid relative to a small crop.
/// Locate that exact pixel crop in the same immutable save_image frame. This
/// preserves native star selection without interpreting multi-star corrections
/// as coordinates, choosing another star, or adding an exposure/setting change.
/// </summary>
public sealed record Phd2PrimaryStarImage(long Frame, int Width, int Height,
    Phd2Point PositionInCrop, ushort[] Pixels)
{
    internal static Phd2PrimaryStarImage? Parse(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object ||
            !result.TryGetProperty("frame", out var f) || f.ValueKind != JsonValueKind.Number || !f.TryGetInt64(out var frame) || frame < 0 ||
            !result.TryGetProperty("width", out var w) || w.ValueKind != JsonValueKind.Number || !w.TryGetInt32(out var width) ||
            !result.TryGetProperty("height", out var h) || h.ValueKind != JsonValueKind.Number || !h.TryGetInt32(out var height) ||
            width is < 1 or > 63 || height is < 1 or > 63 ||
            !result.TryGetProperty("star_pos", out var pos) || pos.ValueKind != JsonValueKind.Array || pos.GetArrayLength() != 2 ||
            pos[0].ValueKind != JsonValueKind.Number || pos[1].ValueKind != JsonValueKind.Number ||
            !pos[0].TryGetDouble(out var x) || !pos[1].TryGetDouble(out var y) ||
            !result.TryGetProperty("pixels", out var encoded) || encoded.ValueKind != JsonValueKind.String)
            return null;
        var bytes = new byte[width * height * 2];
        if (!Convert.TryFromBase64String(encoded.GetString()!, bytes, out var length) || length != bytes.Length)
            return null;
        var pixels = new ushort[width * height];
        for (var i = 0; i < pixels.Length; i++)
            pixels[i] = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i * 2, 2));
        return new(frame, width, height, new(x, y), pixels);
    }

    public Phd2Point? LocateIn(int width, int height, ReadOnlySpan<ushort> pixels)
    {
        if (Width is < 1 or > 63 || Height is < 1 or > 63 || Width > width || Height > height ||
            (long)width * height != pixels.Length || Pixels.Length != Width * Height ||
            !double.IsFinite(PositionInCrop.X) || !double.IsFinite(PositionInCrop.Y) ||
            PositionInCrop.X < 0 || PositionInCrop.X >= Width || PositionInCrop.Y < 0 || PositionInCrop.Y >= Height ||
            Pixels.All(p => p == Pixels[0])) return null;
        Phd2Point? match = null;
        for (var y = 0; y <= height - Height; y++)
        for (var x = 0; x <= width - Width; x++)
        {
            var offset = y * width + x;
            if (!pixels.Slice(offset, Width).SequenceEqual(Pixels.AsSpan(0, Width))) continue;
            var equal = true;
            for (var row = 1; row < Height && equal; row++)
                equal = pixels.Slice(offset + row * width, Width).SequenceEqual(Pixels.AsSpan(row * Width, Width));
            if (!equal) continue;
            if (match is not null) return null; // No ambiguous crop placement.
            match = new(x + PositionInCrop.X, y + PositionInCrop.Y);
        }
        return match;
    }
}
