using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UvexAdv.Commissioning.Tool;

public sealed record WrittenArtifact(string AbsolutePath, string Sha256, long Length);
public sealed record WrittenEvidenceBundle<TBindings>(WrittenArtifact Artifact, TBindings Bindings);

public static class ArtifactIO
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static readonly JsonSerializerOptions CanonicalJsonOptions = new();

    // Production RealCommissioningPresetLoader uses the default numeric enum
    // representation. Keep this separate from JsonOptions, whose string-enum
    // converter is required by human-authored Night Setup definitions.
    public static readonly JsonSerializerOptions CommissioningPresetJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static async Task<WrittenArtifact> WriteJsonAtomicallyAsync<T>(
        string path,
        T value,
        bool overwrite,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        return await WriteBytesAtomicallyAsync(path, bytes, overwrite, cancellationToken).ConfigureAwait(false);
    }

    public static void EnsureBundleWritable(string primaryPath, bool overwrite, params string[] suffixes)
    {
        if (overwrite) return;
        var absolutePath = Path.GetFullPath(primaryPath);
        var conflicts = new[] { absolutePath }
            .Concat(suffixes.Select(suffix => absolutePath + suffix))
            .Where(File.Exists)
            .ToArray();
        if (conflicts.Length > 0)
        {
            throw new IOException($"Refusing to overwrite existing evidence artifact(s): {string.Join(", ", conflicts)}");
        }
    }

    /// <summary>
    /// Writes an immutable three-file evidence bundle. The binding JSON and
    /// hash sidecar are committed first and the primary JSON is committed last;
    /// therefore the existence of the primary path is the bundle commit marker.
    /// Every individual rename is atomic, and failures in this process remove
    /// only files successfully written by this invocation.
    /// </summary>
    public static async Task<WrittenEvidenceBundle<TBindings>> WriteEvidenceBundleAtomicallyAsync<TPrimary, TBindings>(
        string primaryPath,
        TPrimary primaryValue,
        Func<WrittenArtifact, TBindings> createBindings,
        CancellationToken cancellationToken = default,
        JsonSerializerOptions? primaryJsonOptions = null)
    {
        ArgumentNullException.ThrowIfNull(createBindings);
        var absolutePath = Path.GetFullPath(primaryPath);
        var directory = Path.GetDirectoryName(absolutePath)
            ?? throw new InvalidOperationException($"Output path has no parent directory: {absolutePath}");
        Directory.CreateDirectory(directory);
        var lockPath = Path.Combine(directory, $".{Path.GetFileName(absolutePath)}.bundle.lock");
        await using var bundleLock = new FileStream(
            lockPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            1,
            FileOptions.DeleteOnClose);

        EnsureBundleWritable(absolutePath, overwrite: false, ".sha256", ".bindings.json");
        var primaryBytes = JsonSerializer.SerializeToUtf8Bytes(primaryValue, primaryJsonOptions ?? JsonOptions);
        var artifact = new WrittenArtifact(
            absolutePath,
            Convert.ToHexString(SHA256.HashData(primaryBytes)),
            primaryBytes.Length);
        var bindings = createBindings(artifact);
        var bindingPath = absolutePath + ".bindings.json";
        var hashPath = absolutePath + ".sha256";
        var bindingWritten = false;
        var hashWritten = false;
        var primaryWritten = false;
        try
        {
            await WriteJsonAtomicallyAsync(bindingPath, bindings, overwrite: false, cancellationToken).ConfigureAwait(false);
            bindingWritten = true;
            var hashText = $"{artifact.Sha256}  {Path.GetFileName(artifact.AbsolutePath)}{Environment.NewLine}";
            await WriteTextAtomicallyAsync(hashPath, hashText, overwrite: false, cancellationToken).ConfigureAwait(false);
            hashWritten = true;
            await WriteBytesAtomicallyAsync(absolutePath, primaryBytes, overwrite: false, cancellationToken).ConfigureAwait(false);
            primaryWritten = true;
            return new WrittenEvidenceBundle<TBindings>(artifact, bindings);
        }
        catch
        {
            if (primaryWritten) TryDelete(absolutePath);
            if (hashWritten) TryDelete(hashPath);
            if (bindingWritten) TryDelete(bindingPath);
            throw;
        }
    }

    public static async Task<WrittenArtifact> WriteTextAtomicallyAsync(
        string path,
        string text,
        bool overwrite,
        CancellationToken cancellationToken = default) =>
        await WriteBytesAtomicallyAsync(path, new UTF8Encoding(false).GetBytes(text), overwrite, cancellationToken).ConfigureAwait(false);

    public static async Task<WrittenArtifact> WriteBytesAtomicallyAsync(
        string path,
        ReadOnlyMemory<byte> bytes,
        bool overwrite,
        CancellationToken cancellationToken = default)
    {
        var absolutePath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(absolutePath)
            ?? throw new InvalidOperationException($"Output path has no parent directory: {absolutePath}");
        Directory.CreateDirectory(directory);

        if (File.Exists(absolutePath) && !overwrite)
        {
            throw new IOException($"Refusing to overwrite existing evidence artifact: {absolutePath}");
        }

        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(absolutePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(absolutePath))
            {
                if (!overwrite) throw new IOException($"Evidence artifact appeared while writing: {absolutePath}");
                File.Move(temporaryPath, absolutePath, overwrite: true);
            }
            else
            {
                File.Move(temporaryPath, absolutePath);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }

        var hash = Convert.ToHexString(SHA256.HashData(bytes.Span));
        return new WrittenArtifact(absolutePath, hash, bytes.Length);
    }

    public static async Task<byte[]> ReadAndVerifyAsync(
        string path,
        string expectedSha256,
        CancellationToken cancellationToken = default)
    {
        var absolutePath = Path.GetFullPath(path);
        if (!File.Exists(absolutePath)) throw new FileNotFoundException("Evidence artifact was not found.", absolutePath);
        var normalizedExpected = NormalizeHash(expectedSha256);
        if (!IsSha256(normalizedExpected)) throw new InvalidDataException("An explicit 64-character SHA-256 is required.");
        var bytes = await File.ReadAllBytesAsync(absolutePath, cancellationToken).ConfigureAwait(false);
        var actual = Convert.ToHexString(SHA256.HashData(bytes));
        if (!string.Equals(actual, normalizedExpected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"SHA-256 mismatch for {absolutePath}. Expected {normalizedExpected}, actual {actual}.");
        }
        return bytes;
    }

    public static string ComputeFileSha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.GetFullPath(path))));

    public static string NormalizeHash(string value) =>
        (value ?? string.Empty).Replace("-", string.Empty, StringComparison.Ordinal).Trim().ToUpperInvariant();

    public static bool IsSha256(string value) =>
        value.Length == 64 && value.All(static character => Uri.IsHexDigit(character));

    public static async Task WriteHashSidecarAsync(
        WrittenArtifact artifact,
        bool overwrite,
        CancellationToken cancellationToken = default)
    {
        var content = $"{artifact.Sha256}  {Path.GetFileName(artifact.AbsolutePath)}{Environment.NewLine}";
        await WriteTextAtomicallyAsync(artifact.AbsolutePath + ".sha256", content, overwrite, cancellationToken).ConfigureAwait(false);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch { /* Preserve the original write failure; an incomplete bundle has no primary commit marker. */ }
    }
}
