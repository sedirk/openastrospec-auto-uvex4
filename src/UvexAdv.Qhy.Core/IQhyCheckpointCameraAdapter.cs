namespace UvexAdv.Qhy.Core;

/// <summary>Owners with multi-action capture preparation recheck the current
/// job's pause/cancellation/lease immediately before each new hardware action.</summary>
public interface IQhyCheckpointCameraAdapter : IQhyCameraAdapter
{
    Task<QhyFrame> CaptureWithCheckpointAsync(QhyFrameSettings settings,
        Func<CancellationToken, Task> checkpoint, CancellationToken cancellationToken);
}
