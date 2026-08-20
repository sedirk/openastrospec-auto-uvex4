using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace UvexAdv.Qhy.Core;

public sealed record QhyFitsReadResult(
    int Width,
    int Height,
    ushort[] Pixels,
    IReadOnlyDictionary<string, string> Header);

public static class QhyFitsCodec
{
    private const int FitsBlockSize = 2_880;

    public static async Task<string> WriteAsync(
        string path,
        QhyFrame frame,
        Guid jobId,
        string observationRunId,
        Guid frameId,
        int sequenceNumber,
        string role,
        string requestedTarget,
        double? targetRightAscensionDegrees,
        double? targetDeclinationDegrees,
        string coordinateEpoch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new ArgumentException("FITS path has no parent directory.", nameof(path)));
        var temporaryPath = path + ".partial";
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             1 << 20,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var cards = BuildHeader(
                    frame,
                    jobId,
                    observationRunId,
                    frameId,
                    sequenceNumber,
                    role,
                    requestedTarget,
                    targetRightAscensionDegrees,
                    targetDeclinationDegrees,
                    coordinateEpoch);
                var header = Encoding.ASCII.GetBytes(string.Concat(cards));
                await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
                await PadToFitsBlockAsync(stream, header.Length, 0x20, cancellationToken).ConfigureAwait(false);

                var buffer = new byte[Math.Min(1 << 20, frame.Pixels.Length * 2)];
                var pixelIndex = 0;
                while (pixelIndex < frame.Pixels.Length)
                {
                    var pixelsThisBlock = Math.Min(buffer.Length / 2, frame.Pixels.Length - pixelIndex);
                    for (var index = 0; index < pixelsThisBlock; index++)
                    {
                        var signed = unchecked((short)(frame.Pixels[pixelIndex + index] - 32_768));
                        BinaryPrimitives.WriteInt16BigEndian(buffer.AsSpan(index * 2, 2), signed);
                    }

                    await stream.WriteAsync(buffer.AsMemory(0, pixelsThisBlock * 2), cancellationToken).ConfigureAwait(false);
                    pixelIndex += pixelsThisBlock;
                }

                await PadToFitsBlockAsync(stream, frame.Pixels.Length * 2, 0, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, path, overwrite: false);
            await using var completed = File.OpenRead(path);
            return Convert.ToHexString(await SHA256.HashDataAsync(completed, cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public static QhyFitsReadResult Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var header = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var cardBytes = new byte[80];
        var headerBytes = 0;
        while (true)
        {
            stream.ReadExactly(cardBytes);
            headerBytes += cardBytes.Length;
            var card = Encoding.ASCII.GetString(cardBytes);
            var keyword = card[..8].Trim();
            if (keyword == "END") break;
            if (card.Length >= 10 && card[8] == '=') header[keyword] = ParseCardValue(card[10..]);
        }

        var remainingHeaderPadding = (FitsBlockSize - (headerBytes % FitsBlockSize)) % FitsBlockSize;
        if (remainingHeaderPadding > 0) stream.Seek(remainingHeaderPadding, SeekOrigin.Current);
        var width = ParseRequiredInt(header, "NAXIS1");
        var height = ParseRequiredInt(header, "NAXIS2");
        var bitpix = ParseRequiredInt(header, "BITPIX");
        if (bitpix != 16) throw new InvalidDataException($"Only 16-bit primary FITS images are supported for replay; BITPIX={bitpix}.");
        var bzero = ParseDouble(header.GetValueOrDefault("BZERO"), 0);
        var bscale = ParseDouble(header.GetValueOrDefault("BSCALE"), 1);
        var pixels = new ushort[checked(width * height)];
        var data = new byte[Math.Min(1 << 20, pixels.Length * 2)];
        var pixelIndex = 0;
        while (pixelIndex < pixels.Length)
        {
            var pixelsThisBlock = Math.Min(data.Length / 2, pixels.Length - pixelIndex);
            stream.ReadExactly(data.AsSpan(0, pixelsThisBlock * 2));
            for (var index = 0; index < pixelsThisBlock; index++)
            {
                var signed = BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(index * 2, 2));
                pixels[pixelIndex + index] = (ushort)Math.Clamp(Math.Round((signed * bscale) + bzero), 0, ushort.MaxValue);
            }

            pixelIndex += pixelsThisBlock;
        }

        return new QhyFitsReadResult(width, height, pixels, header);
    }

    private static IReadOnlyList<string> BuildHeader(
        QhyFrame frame,
        Guid jobId,
        string observationRunId,
        Guid frameId,
        int sequenceNumber,
        string role,
        string requestedTarget,
        double? targetRightAscensionDegrees,
        double? targetDeclinationDegrees,
        string coordinateEpoch)
    {
        var settings = frame.Settings;
        var cards = new List<string>
        {
            Card("SIMPLE", true, "conforms to FITS standard"),
            Card("BITPIX", 16, "array data type"),
            Card("NAXIS", 2, "number of array dimensions"),
            Card("NAXIS1", frame.Width, "image width"),
            Card("NAXIS2", frame.Height, "image height"),
            Card("BZERO", 32768, "unsigned 16-bit offset"),
            Card("BSCALE", 1, "data scaling"),
            Card("EXTEND", true, "extensions may be present"),
            Card("BUNIT", "adu", "pixel unit"),
            Card("TIMESYS", "UTC", "time reference"),
            Card("INSTRUME", frame.Identity.Model, "imaging instrument"),
            Card("CAMERAID", frame.Identity.StableId, "stable camera identifier"),
            Card("QHYADAPT", frame.Identity.Adapter, "camera adapter"),
            Card("DATE-OBS", FitsTimestamp(frame.ExposureStartedUtc), "exposure start UTC"),
            Card("DATE-AVG", FitsTimestamp(frame.MidpointUtc), "exposure midpoint UTC"),
            Card("DATE-END", FitsTimestamp(frame.ExposureEndedUtc), "exposure end UTC"),
            Card("EXPTIME", settings.ExposureSeconds, "exposure seconds"),
            Card("GAIN", settings.Gain, "sensor gain"),
            Card("OFFSET", settings.Offset, "sensor offset"),
            Card("XBINNING", settings.BinningX, "horizontal binning"),
            Card("YBINNING", settings.BinningY, "vertical binning"),
            Card("XORGSUBF", settings.RoiX, "ROI origin X"),
            Card("YORGSUBF", settings.RoiY, "ROI origin Y"),
            Card("READMODE", settings.ReadoutMode, "QHY readout mode index"),
            Card("BITDEPTH", settings.BitDepth, "requested output bit depth"),
            Card("USBLIMIT", settings.UsbTraffic, "QHY USB traffic setting"),
            Card("FILTER", settings.FilterName, "filter name"),
            Card("IMAGETYP", "LIGHT", "frame type"),
            Card("FRAMROLE", role, "acquisition or photometry"),
            Card("FRAMESEQ", sequenceNumber, "sequence within job"),
            Card("OBJECT", requestedTarget, "requested catalog target"),
            Card("OBS-RUN", observationRunId, "observation run identifier"),
            Card("JOBID", jobId.ToString("D"), "QHY job identifier"),
            Card("FRAMEID", frameId.ToString("D"), "frame identifier"),
            Card("SWCREATE", "UVEX-ADV QHY service", "acquisition software"),
        };
        if (targetRightAscensionDegrees is { } ra && targetDeclinationDegrees is { } dec)
        {
            cards.Add(Card("RA_DEG", ra, "requested ICRS RA degrees"));
            cards.Add(Card("DEC_DEG", dec, "requested ICRS Dec degrees"));
            cards.Add(Card("RADESYS", coordinateEpoch, "requested coordinate frame"));
            if (string.Equals(coordinateEpoch, "ICRS", StringComparison.OrdinalIgnoreCase))
            {
                cards.Add(Card("EQUINOX", 2000d, "coordinate reference equinox"));
            }
        }
        cards.Add(EndCard());
        return cards;
    }

    private static string Card(string keyword, string value, string comment) =>
        FormatCard(keyword, $"'{EscapeFitsString(value)}'", comment);

    private static string Card(string keyword, int value, string comment) =>
        FormatCard(keyword, value.ToString(CultureInfo.InvariantCulture), comment);

    private static string Card(string keyword, double value, string comment) =>
        FormatCard(keyword, value.ToString("G15", CultureInfo.InvariantCulture), comment);

    private static string Card(string keyword, bool value, string comment) =>
        FormatCard(keyword, value ? "T" : "F", comment);

    private static string FormatCard(string keyword, string value, string comment)
    {
        var normalizedKeyword = keyword.Length <= 8 ? keyword.PadRight(8) : keyword[..8];
        var baseCard = $"{normalizedKeyword}= {value.PadLeft(20)}";
        if (baseCard.Length > 80) throw new InvalidDataException($"FITS value for {keyword} exceeds one card.");
        var card = $"{baseCard} / {comment}";
        return (card.Length > 80 ? baseCard : card).PadRight(80);
    }

    private static string EndCard() => "END".PadRight(80);

    private static string FitsTimestamp(DateTimeOffset timestamp) =>
        timestamp.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture);

    private static string EscapeFitsString(string value)
    {
        var escaped = new StringBuilder(Math.Min(64, value.Length));
        foreach (var character in value)
        {
            var required = character == '\'' ? 2 : 1;
            if (escaped.Length + required > 64) break;
            escaped.Append(character);
            if (character == '\'') escaped.Append('\'');
        }

        return escaped.ToString();
    }

    private static async Task PadToFitsBlockAsync(
        Stream stream,
        int bytesWritten,
        byte paddingValue,
        CancellationToken cancellationToken)
    {
        var padding = (FitsBlockSize - (bytesWritten % FitsBlockSize)) % FitsBlockSize;
        if (padding == 0) return;
        var buffer = new byte[padding];
        if (paddingValue != 0) Array.Fill(buffer, paddingValue);
        await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    private static string ParseCardValue(string valueAndComment)
    {
        var value = valueAndComment.Split('/', 2)[0].Trim();
        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
        {
            return value[1..^1].Replace("''", "'", StringComparison.Ordinal);
        }

        return value;
    }

    private static int ParseRequiredInt(IReadOnlyDictionary<string, string> header, string key) =>
        header.TryGetValue(key, out var value) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : throw new InvalidDataException($"FITS header does not contain a valid {key}.");

    private static double ParseDouble(string? value, double fallback) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : fallback;
}
