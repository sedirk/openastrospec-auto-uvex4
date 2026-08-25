using System.IO;
using System.Text;

namespace UvexAdv.Nina.Plugin;

/// <summary>
/// Read-only, header-only verification of an immutable FITS immediately after
/// N.I.N.A. reports that it has saved the image.
/// </summary>
internal static class AtrFitsProvenance
{
    private const int FitsCardLength = 80;
    private const int FitsBlockLength = 2880;

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
        Require(headers, "OBJECT", expected.TargetName, issues);
        Require(headers, "OBSRUNID", expected.ObservationRunId, issues);
        Require(headers, "UVEXSTG", expected.StageRole, issues);
        Require(headers, "UVEXCID", expected.CaptureId, issues);
        Require(headers, "NIGHTSET", expected.NightSetupId, issues);
        Require(headers, "IMAGETYP", expected.ImageType, issues);
        if (!string.IsNullOrWhiteSpace(expected.CatalogId))
        {
            Require(headers, "CATALOG", expected.CatalogId, issues);
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
    string CatalogId);

internal sealed record FitsProvenanceVerification(
    bool IsValid,
    IReadOnlyList<string> Issues,
    IReadOnlyDictionary<string, string> Headers);
