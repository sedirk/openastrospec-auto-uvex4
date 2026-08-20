namespace UvexAdv.Qhy.Core;

public interface IQhyCameraAdapter : IAsyncDisposable
{
    string AdapterName { get; }

    QhyCameraStatus Status { get; }

    Task<QhyCameraIdentity> ConnectExactAsync(
        string expectedStableId,
        string expectedModel,
        CancellationToken cancellationToken);

    Task DisconnectAsync(CancellationToken cancellationToken);

    Task<QhyFilterWheelStatus> ReadFilterWheelStatusAsync(CancellationToken cancellationToken);

    Task<QhyFilterWheelStatus> SelectFilterAsync(string filterName, CancellationToken cancellationToken);

    Task<QhyFrame> CaptureSingleFrameAsync(QhyFrameSettings settings, CancellationToken cancellationToken);
}

public sealed class QhyAdapterException(string message, Exception? innerException = null)
    : Exception(message, innerException);
