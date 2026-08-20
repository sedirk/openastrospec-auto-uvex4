using System.Security.Cryptography;
using System.Text.Json;

namespace UvexAdv.Observatory;

public sealed record EvidenceReference(
    string Kind,
    string AbsolutePath,
    string Sha256,
    DateTimeOffset TimestampUtc,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record ObservationManifest(
    int SchemaVersion,
    ObservationPlan Plan,
    ObservationSnapshot Snapshot,
    IReadOnlyList<EvidenceReference> Evidence,
    DateTimeOffset WrittenUtc);

public static class ObservationManifestWriter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static async Task<EvidenceReference> DescribeEvidenceAsync(
        string kind,
        string absolutePath,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        if (!Path.IsPathFullyQualified(absolutePath)) throw new ArgumentException("Evidence path must be absolute.", nameof(absolutePath));
        await using var stream = File.OpenRead(absolutePath);
        var digest = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return new EvidenceReference(kind, absolutePath, Convert.ToHexString(digest).ToLowerInvariant(), File.GetLastWriteTimeUtc(absolutePath), metadata);
    }

    public static async Task WriteAtomicAsync(string path, ObservationManifest manifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath) ?? throw new ArgumentException("Manifest path has no parent directory.", nameof(path));
        Directory.CreateDirectory(directory);
        var temporaryPath = fullPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, manifest, Options, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporaryPath, fullPath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}
