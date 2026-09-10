namespace UvexAdv.Qhy.Core;

/// <summary>A camera owner may save through its native pipeline. The store only
/// indexes and hashes that immutable file; it never makes another raw FITS.</summary>
public interface IQhyNativeFramePersistence
{
    Task<string> SaveNativeFrameAsync(QhyJobSnapshot job, QhyFrame frame,
        Guid frameId, int sequenceNumber, string role, CancellationToken cancellationToken);
}
