using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

internal sealed record StellariumCompanionReference(string CatalogId, double SeparationArcseconds,
    double PositionAngleDegrees, int CatalogueYear, string ResponseSha256, DateTimeOffset ReadUtc);

internal static class StellariumCompanionReferenceReader
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(5), MaxResponseContentBufferSize = 65536 };

    internal static async Task<StellariumCompanionReference?> ReadAsync(Uri endpoint, string catalogId,
        double raDegrees, double decDegrees, CancellationToken token)
    {
        try
        {
            var bytes = await Client.GetByteArrayAsync(new Uri(endpoint, "api/objects/info?format=json"), token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(bytes);
            return Parse(document.RootElement, catalogId, raDegrees, decDegrees,
                Convert.ToHexString(SHA256.HashData(bytes)), DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException or FormatException)
        {
            return null; // Optional reference absent: original ambiguity remains, never a star substitution.
        }
    }

    internal static StellariumCompanionReference? Parse(JsonElement root, string catalogId,
        double raDegrees, double decDegrees, string sha256, DateTimeOffset now)
    {
        // A component suffix cannot inherit the system's primary-to-secondary
        // direction. Only an exact unsuffixed HIP primary record is supported.
        var normalizedId = catalogId.Trim().ToUpperInvariant();
        if (!System.Text.RegularExpressions.Regex.IsMatch(normalizedId, @"^HIP [0-9]+$")) return null;
        if (!root.TryGetProperty("name", out var name) || name.GetString()?.Trim().ToUpperInvariant() != normalizedId
            || !root.TryGetProperty("type", out var type) || type.GetString() != "Star"
            || !root.TryGetProperty("star-type", out var starType) || starType.GetString() != "double-star") return null;
        bool Read(string key, out double value)
        {
            value = double.NaN;
            return root.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.Number
                && item.TryGetDouble(out value) && double.IsFinite(value);
        }
        if (!Read("raJ2000", out var ra) || !Read("decJ2000", out var dec)
            || !Read("wds-separation", out var separation) || !Read("wds-position-angle", out var angle)
            || !Read("wds-year", out var year) || year != Math.Truncate(year)
            || year < now.Year - 10 || year > now.Year || separation is < 3 or > 30 || angle is < 0 or >= 360
            || !double.IsFinite(raDegrees) || !double.IsFinite(decDegrees)
            || ra is < 0 or >= 360 || dec is < -90 or > 90
            || G3AcquisitionMotionPlanner.AngularSeparationArcseconds(ra, dec, raDegrees, decDegrees) > 5) return null;
        return new(normalizedId, separation, angle, (int)year, sha256, now);
    }
}
