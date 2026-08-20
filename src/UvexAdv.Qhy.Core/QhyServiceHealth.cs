using System.Security.Cryptography;
using System.Text.Json;

namespace UvexAdv.Qhy.Core;

/// <summary>
/// Non-secret, canonical proof of the QHY service configuration that selects
/// the physical adapter. Paths and credentials are deliberately excluded.
/// </summary>
public sealed record QhyServiceConfigurationProof(
    int SchemaVersion,
    bool Simulator,
    string Adapter,
    string ExpectedModel,
    string ExpectedStableId,
    string NativeSdkSha256,
    int NativeReadoutMode,
    string NativeFilterPositionsSha256,
    string ConfigurationSha256)
{
    public const int CurrentSchemaVersion = 2;

    public static QhyServiceConfigurationProof Create(
        bool simulator,
        string adapter,
        string expectedModel,
        string expectedStableId,
        string? nativeSdkSha256,
        int nativeReadoutMode,
        IReadOnlyDictionary<string, int>? nativeFilterPositions = null)
    {
        var normalizedAdapter = NormalizeRequired(adapter, nameof(adapter)).ToLowerInvariant();
        var normalizedModel = NormalizeRequired(expectedModel, nameof(expectedModel));
        var normalizedStableId = NormalizeRequired(expectedStableId, nameof(expectedStableId));
        var normalizedSdkHash = NormalizeOptionalHash(nativeSdkSha256);
        if (!simulator && !IsSha256(normalizedSdkHash))
        {
            throw new ArgumentException(
                "A 64-character native SDK SHA-256 is required for the hardware QHY adapter.",
                nameof(nativeSdkSha256));
        }
        if (nativeReadoutMode < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(nativeReadoutMode),
                "The native QHY readout mode must be zero or greater.");
        }

        ValidateFilterPositions(nativeFilterPositions, requireConfigured: !simulator);

        var filterPositionsSha256 = ComputeFilterPositionsSha256(nativeFilterPositions);

        var configurationSha256 = ComputeCanonicalSha256(
            CurrentSchemaVersion,
            simulator,
            normalizedAdapter,
            normalizedModel,
            normalizedStableId,
            normalizedSdkHash,
            nativeReadoutMode,
            filterPositionsSha256);
        return new QhyServiceConfigurationProof(
            CurrentSchemaVersion,
            simulator,
            normalizedAdapter,
            normalizedModel,
            normalizedStableId,
            normalizedSdkHash,
            nativeReadoutMode,
            filterPositionsSha256,
            configurationSha256);
    }

    public IReadOnlyList<string> Validate()
    {
        var issues = new List<string>();
        if (SchemaVersion != CurrentSchemaVersion)
        {
            issues.Add($"Unsupported QHY configuration-proof schema {SchemaVersion}.");
        }
        if (string.IsNullOrWhiteSpace(Adapter)) issues.Add("QHY adapter is missing.");
        if (string.IsNullOrWhiteSpace(ExpectedModel)) issues.Add("QHY expected model is missing.");
        if (string.IsNullOrWhiteSpace(ExpectedStableId)) issues.Add("QHY expected stable ID is missing.");
        if (!string.IsNullOrWhiteSpace(NativeSdkSha256) && !IsSha256(NormalizeOptionalHash(NativeSdkSha256)))
        {
            issues.Add("QHY native SDK SHA-256 is malformed.");
        }
        if (!Simulator && !IsSha256(NormalizeOptionalHash(NativeSdkSha256)))
        {
            issues.Add("Hardware QHY configuration is missing a valid native SDK SHA-256.");
        }
        if (NativeReadoutMode < 0) issues.Add("QHY native readout mode must be zero or greater.");
        if (!IsSha256(NormalizeOptionalHash(NativeFilterPositionsSha256)))
        {
            issues.Add("QHY native filter-position map SHA-256 is malformed.");
        }
        if (!IsSha256(NormalizeOptionalHash(ConfigurationSha256)))
        {
            issues.Add("QHY canonical configuration SHA-256 is malformed.");
            return issues;
        }

        if (SchemaVersion == CurrentSchemaVersion &&
            !string.IsNullOrWhiteSpace(Adapter) &&
            !string.IsNullOrWhiteSpace(ExpectedModel) &&
            !string.IsNullOrWhiteSpace(ExpectedStableId))
        {
            var computed = ComputeCanonicalSha256(
                SchemaVersion,
                Simulator,
                Adapter.Trim().ToLowerInvariant(),
                ExpectedModel.Trim(),
                ExpectedStableId.Trim(),
                NormalizeOptionalHash(NativeSdkSha256),
                NativeReadoutMode,
                NormalizeOptionalHash(NativeFilterPositionsSha256));
            if (!string.Equals(computed, NormalizeOptionalHash(ConfigurationSha256), StringComparison.Ordinal))
            {
                issues.Add("QHY canonical configuration SHA-256 does not match the advertised fields.");
            }
        }
        return issues;
    }

    public static bool IsSha256(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length == 64 &&
        value.All(Uri.IsHexDigit);

    public static string ComputeFilterPositionsSha256(IReadOnlyDictionary<string, int>? filterPositions)
    {
        var normalized = new SortedDictionary<string, int>(StringComparer.Ordinal);
        if (filterPositions is not null)
        {
            foreach (var (name, position) in filterPositions)
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    throw new ArgumentException("QHY native filter names must not be empty.", nameof(filterPositions));
                }
                if (position < 0)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(filterPositions),
                        $"QHY native filter position '{name}' must be zero or greater.");
                }

                var normalizedName = name.ToLowerInvariant();
                if (!normalized.TryAdd(normalizedName, position))
                {
                    throw new ArgumentException(
                        $"QHY native filter name '{name}' is duplicated under case-insensitive matching.",
                        nameof(filterPositions));
                }
            }
        }

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            foreach (var (name, position) in normalized)
            {
                writer.WriteNumber(name, position);
            }
            writer.WriteEndObject();
            writer.Flush();
        }
        return Convert.ToHexString(SHA256.HashData(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length))));
    }

    public static void ValidateFilterPositions(
        IReadOnlyDictionary<string, int>? filterPositions,
        bool requireConfigured)
    {
        if (requireConfigured && (filterPositions is null || filterPositions.Count == 0))
        {
            throw new ArgumentException(
                "Hardware QHY mode requires an explicit integrated filter-wheel position map.",
                nameof(filterPositions));
        }

        if (filterPositions is null) return;
        var positions = new HashSet<int>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (rawName, position) in filterPositions)
        {
            var name = rawName?.Trim() ?? string.Empty;
            if (name.Length == 0)
            {
                throw new ArgumentException("QHY native filter names must not be empty.", nameof(filterPositions));
            }
            if (!names.Add(name))
            {
                throw new ArgumentException(
                    $"QHY native filter name '{rawName}' is duplicated under case-insensitive matching.",
                    nameof(filterPositions));
            }
            if (position is < 0 or > 15)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(filterPositions),
                    $"QHY native filter position '{rawName}' must be within 0-15.");
            }
            if (!positions.Add(position))
            {
                throw new ArgumentException(
                    $"QHY native filter position {position} is assigned more than once; the live wheel state would be ambiguous.",
                    nameof(filterPositions));
            }
        }
    }

    private static string ComputeCanonicalSha256(
        int schemaVersion,
        bool simulator,
        string adapter,
        string expectedModel,
        string expectedStableId,
        string nativeSdkSha256,
        int nativeReadoutMode,
        string nativeFilterPositionsSha256)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", schemaVersion);
            writer.WriteBoolean("simulator", simulator);
            writer.WriteString("adapter", adapter);
            writer.WriteString("expectedModel", expectedModel);
            writer.WriteString("expectedStableId", expectedStableId);
            writer.WriteString("nativeSdkSha256", nativeSdkSha256);
            writer.WriteNumber("nativeReadoutMode", nativeReadoutMode);
            writer.WriteString("nativeFilterPositionsSha256", nativeFilterPositionsSha256);
            writer.WriteEndObject();
            writer.Flush();
        }
        return Convert.ToHexString(SHA256.HashData(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length))));
    }

    private static string NormalizeRequired(string value, string name) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A non-empty value is required.", name)
            : value.Trim();

    private static string NormalizeOptionalHash(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Replace("-", string.Empty, StringComparison.Ordinal).Trim().ToUpperInvariant();
}

public sealed record QhyServiceHealth(
    string Service,
    string Status,
    bool LoopbackOnly,
    QhyServiceConfigurationProof Configuration,
    DateTimeOffset TimestampUtc);
