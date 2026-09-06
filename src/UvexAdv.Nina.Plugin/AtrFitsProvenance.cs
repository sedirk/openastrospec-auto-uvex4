using System.IO;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using NINA.Core.Utility;

namespace UvexAdv.Nina.Plugin;

/// <summary>
/// Read-only, header-only verification of an immutable FITS immediately after
/// N.I.N.A. reports that it has saved the image.
/// </summary>
internal static class AtrFitsProvenance
{
    private const int FitsCardLength = 80;
    private const int FitsBlockLength = 2880;

    // FITS cards are not Unicode strings. Keep readable bounded ASCII aliases
    // and independently verify lossless UTF-8 fields, split before NINA saves.
    internal static string FitsTargetName(FitsProvenanceExpectation expected) =>
        CompactAscii(expected.TargetName, expected.CatalogId);

    internal static IReadOnlyDictionary<string, string> CreateIdentityHeaders(FitsProvenanceExpectation expected)
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["UVEXPV"] = "2",
            ["OBSRUNID"] = expected.ObservationRunId,
            ["UVEXSTG"] = expected.StageRole,
            ["UVEXCID"] = expected.CaptureId,
            ["NIGHTSET"] = CompactAscii(expected.NightSetupId),
            ["NINATYP"] = expected.ImageType,
        };
        if (!string.IsNullOrWhiteSpace(expected.CatalogId)) headers["CATALOG"] = CompactAscii(expected.CatalogId);
        AddUtf8(headers, "OBJ", expected.TargetName);
        AddUtf8(headers, "NST", expected.NightSetupId);
        AddUtf8(headers, "CAT", expected.CatalogId);
        if (headers.Any(item => item.Key.Length > 8 || item.Value.Any(c => c < 32 || c > 126) ||
            item.Value.Replace("'", "''", StringComparison.Ordinal).Length > 60))
        {
            throw new InvalidDataException("ATR provenance contains a field that cannot fit an ASCII FITS card.");
        }
        return headers;
    }

    private static string CompactAscii(string value, string fallback = "")
    {
        var native = TextEncoding.GreekToLatinAbbreviation(value);
        var ascii = new string(native.Where(c => c >= 32 && c <= 126).ToArray()).Trim();
        if (ascii.Length == 0 && fallback.Length > 0) return CompactAscii(fallback);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..12];
        if (ascii.Length == 0) return "Target-" + hash;
        if (ascii.Replace("'", "''", StringComparison.Ordinal).Length <= 60) return ascii;
        return new string(ascii.Take(40).Select(c => c == '\'' ? '_' : c).ToArray()).TrimEnd() + "-" + hash;
    }

    private static void AddUtf8(IDictionary<string, string> headers, string prefix, string value)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        var count = Math.Max(1, (encoded.Length + 47) / 48);
        if (count > 9999) throw new InvalidDataException("ATR provenance text is too long.");
        headers[prefix + "ENC"] = "UTF8-B64";
        headers[prefix + "CNT"] = count.ToString(CultureInfo.InvariantCulture);
        for (var index = 0; index < count; index++)
        {
            var offset = index * 48;
            headers[prefix + (index + 1).ToString("D4", CultureInfo.InvariantCulture)] =
                encoded.Substring(offset, Math.Min(48, encoded.Length - offset));
        }
    }

    internal static FitsProvenanceVerification Verify(
        string path,
        FitsProvenanceExpectation expected)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(expected);

        IReadOnlyDictionary<string, string> headers;
        try
        {
            headers = ReadPrimaryHeader(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new FitsProvenanceVerification(
                false,
                new[] { $"无法只读复核已保存 FITS：{ex.Message}" },
                new Dictionary<string, string>(StringComparer.Ordinal));
        }

        var issues = new List<string>();
        var versioned = expected.HeaderSchemaVersion == 2;
        Require(headers, "OBJECT", versioned ? FitsTargetName(expected) : expected.TargetName, issues);
        Require(headers, "OBSRUNID", expected.ObservationRunId, issues);
        Require(headers, "UVEXSTG", expected.StageRole, issues);
        Require(headers, "UVEXCID", expected.CaptureId, issues);
        Require(headers, "NIGHTSET", versioned ? CompactAscii(expected.NightSetupId) : expected.NightSetupId, issues);
        // NINA 3.2 FITSHeader.PopulateFromMetaData explicitly writes SNAPSHOT
        // as LIGHT. Preserve the requested capture type separately; PROBE and
        // SCIENCE remain distinct and mandatory in UVEXSTG.
        Require(headers, "IMAGETYP", versioned && expected.ImageType == "SNAPSHOT" ? "LIGHT" : expected.ImageType, issues);
        if (versioned)
        {
            foreach (var field in CreateIdentityHeaders(expected)) Require(headers, field.Key, field.Value, issues);
        }
        if (!string.IsNullOrWhiteSpace(expected.CatalogId))
        {
            Require(headers, "CATALOG", versioned ? CompactAscii(expected.CatalogId) : expected.CatalogId, issues);
        }
        return new FitsProvenanceVerification(issues.Count == 0, issues.AsReadOnly(), headers);
    }

    internal static IReadOnlyDictionary<string, string> ReadPrimaryHeader(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var block = new byte[FitsBlockLength];
        while (true)
        {
            var offset = 0;
            while (offset < block.Length)
            {
                var read = stream.Read(block, offset, block.Length - offset);
                if (read == 0) throw new InvalidDataException("FITS primary header ended before the END card.");
                offset += read;
            }

            for (var cardOffset = 0; cardOffset < block.Length; cardOffset += FitsCardLength)
            {
                var card = Encoding.ASCII.GetString(block, cardOffset, FitsCardLength);
                var key = card[..8].Trim();
                if (string.Equals(key, "END", StringComparison.Ordinal)) return result;
                if (key.Length == 0 || card[8] != '=') continue;
                result[key] = ParseValue(card[10..]);
            }
        }
    }

    private static string ParseValue(string field)
    {
        var value = field.TrimStart();
        if (value.StartsWith('\''))
        {
            var builder = new StringBuilder();
            for (var index = 1; index < value.Length; index++)
            {
                if (value[index] != '\'')
                {
                    builder.Append(value[index]);
                    continue;
                }
                if (index + 1 < value.Length && value[index + 1] == '\'')
                {
                    builder.Append('\'');
                    index++;
                    continue;
                }
                break;
            }
            return builder.ToString().TrimEnd();
        }

        var comment = value.IndexOf('/');
        return (comment >= 0 ? value[..comment] : value).Trim();
    }

    private static void Require(
        IReadOnlyDictionary<string, string> headers,
        string keyword,
        string expected,
        ICollection<string> issues)
    {
        if (!headers.TryGetValue(keyword, out var actual))
        {
            issues.Add($"FITS 缺少 {keyword}。期望值：'{expected}'。");
            return;
        }
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            issues.Add($"FITS {keyword}='{actual}'，期望 '{expected}'。");
        }
    }
}

internal sealed record FitsProvenanceExpectation(
    string TargetName,
    string ObservationRunId,
    string StageRole,
    string CaptureId,
    string NightSetupId,
    string ImageType,
    string CatalogId,
    int HeaderSchemaVersion = 1);

internal sealed record FitsProvenanceVerification(
    bool IsValid,
    IReadOnlyList<string> Issues,
    IReadOnlyDictionary<string, string> Headers);
