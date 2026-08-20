namespace UvexAdv.Protocol;

public interface IUvexTransport : IAsyncDisposable
{
    bool IsOpen { get; }

    Task OpenAsync(CancellationToken cancellationToken);

    Task CloseAsync(CancellationToken cancellationToken);

    Task WriteAsync(string frame, CancellationToken cancellationToken);

    IAsyncEnumerable<string> ReadChunksAsync(CancellationToken cancellationToken);
}
