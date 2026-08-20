using System.IO;
using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

/// <summary>
/// Marker implemented only by the real composite runner. Simulations never
/// participate in the machine-wide equipment-owner lease.
/// </summary>
internal interface IRealObservationRunOwnershipSource
{
    string RealObservationOwnershipLockPath { get; }
}

internal sealed record RealObservationRunOwnershipAcquireResult(
    RealObservationRunOwnershipLease? Lease,
    string? Failure)
{
    public bool Acquired => Lease is not null && Failure is null;
}

/// <summary>
/// An OS-backed exclusive file handle held for the complete lifetime of a
/// real observation RunAsync invocation. FileShare.None provides cross-host
/// and cross-process exclusion, and the operating system releases the handle
/// after a hard process failure. The file's contents are not ownership proof;
/// only the live handle is.
/// </summary>
internal sealed class RealObservationRunOwnershipLease : IDisposable
{
    private FileStream? stream;

    private RealObservationRunOwnershipLease(string path, FileStream stream)
    {
        Path = path;
        this.stream = stream;
    }

    public string Path { get; }

    public static RealObservationRunOwnershipAcquireResult TryAcquire(string path)
    {
        try
        {
            var fullPath = System.IO.Path.GetFullPath(path);
            var directory = System.IO.Path.GetDirectoryName(fullPath)
                ?? throw new InvalidOperationException("The real-observation owner-lock path has no parent directory.");
            Directory.CreateDirectory(directory);
            var handle = new FileStream(
                fullPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);
            return new RealObservationRunOwnershipAcquireResult(
                new RealObservationRunOwnershipLease(fullPath, handle),
                null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return new RealObservationRunOwnershipAcquireResult(
                null,
                $"The machine-wide real-observation owner lease could not be acquired: {ex.Message}");
        }
    }

    public void Dispose() => Interlocked.Exchange(ref stream, null)?.Dispose();
}
