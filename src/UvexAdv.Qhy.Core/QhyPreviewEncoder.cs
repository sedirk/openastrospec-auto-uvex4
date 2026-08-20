using System.Buffers.Binary;
using System.IO.Compression;

namespace UvexAdv.Qhy.Core;

public static class QhyPreviewEncoder
{
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

    public static QhyPreview Encode(Guid jobId, Guid frameId, QhyFrame frame, QhyFrameMetrics metrics, int maximumDimension = 1_600)
    {
        var scale = Math.Min(1.0, maximumDimension / (double)Math.Max(frame.Width, frame.Height));
        var width = Math.Max(1, (int)Math.Round(frame.Width * scale));
        var height = Math.Max(1, (int)Math.Round(frame.Height * scale));
        var displayMinimum = metrics.MedianAdu - (2.5 * metrics.BackgroundSigmaAdu);
        var displayMaximum = Math.Max(displayMinimum + 1, metrics.P999Adu);
        var raw = new byte[checked(height * (width + 1))];

        for (var targetY = 0; targetY < height; targetY++)
        {
            var rowOffset = targetY * (width + 1);
            raw[rowOffset] = 0;
            var sourceY = Math.Min(frame.Height - 1, (int)(targetY / scale));
            for (var targetX = 0; targetX < width; targetX++)
            {
                var sourceX = Math.Min(frame.Width - 1, (int)(targetX / scale));
                var value = frame.Pixels[(sourceY * frame.Width) + sourceX];
                var normalized = Math.Clamp((value - displayMinimum) / (displayMaximum - displayMinimum), 0, 1);
                raw[rowOffset + targetX + 1] = (byte)Math.Round(255 * Math.Asinh(8 * normalized) / Math.Asinh(8));
            }
        }

        using var output = new MemoryStream();
        output.Write(PngSignature);
        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], height);
        ihdr[8] = 8;
        ihdr[9] = 0;
        ihdr[10] = 0;
        ihdr[11] = 0;
        ihdr[12] = 0;
        WriteChunk(output, "IHDR"u8, ihdr);

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true)) zlib.Write(raw);
        WriteChunk(output, "IDAT"u8, compressed.ToArray());
        WriteChunk(output, "IEND"u8, ReadOnlySpan<byte>.Empty);
        return new QhyPreview(jobId, frameId, width, height, displayMinimum, displayMaximum, output.ToArray(), DateTimeOffset.UtcNow);
    }

    private static void WriteChunk(Stream stream, ReadOnlySpan<byte> type, ReadOnlySpan<byte> payload)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, payload.Length);
        stream.Write(length);
        stream.Write(type);
        stream.Write(payload);

        var crcBuffer = new byte[type.Length + payload.Length];
        type.CopyTo(crcBuffer);
        payload.CopyTo(crcBuffer.AsSpan(type.Length));
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(crcBuffer));
        stream.Write(crc);
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xffffffffu;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ (0xedb88320u & (uint)-(int)(crc & 1));
        }

        return ~crc;
    }
}
