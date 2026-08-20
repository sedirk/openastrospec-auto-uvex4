using UvexAdv.Protocol;

namespace UvexAdv.Core;

public sealed class UvexProtocolSession(IUvexTransport transport, TimeSpan commandTimeout) : IAsyncDisposable
{
    private readonly SemaphoreSlim commandGate = new(1, 1);
    private readonly UvexFrameParser parser = new();
    private readonly object pendingGate = new();
    private TaskCompletionSource<UvexFrame>? pending;
    private string? pendingCode;
    private string? pendingWire;
    private bool pendingEcho;
    private CancellationTokenSource? readCts;
    private Task? readTask;

    public event EventHandler<UvexFrame>? UnsolicitedFrame;

    public bool IsOpen => transport.IsOpen;

    public async Task OpenAsync(CancellationToken cancellationToken)
    {
        await transport.OpenAsync(cancellationToken).ConfigureAwait(false);
        readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        readTask = ReadLoopAsync(readCts.Token);
    }

    public async Task CloseAsync(CancellationToken cancellationToken)
    {
        readCts?.Cancel();
        if (readTask is not null)
        {
            try
            {
                await readTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        await transport.CloseAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<UvexFrame?> SendAsync(UvexCommand command, CancellationToken cancellationToken)
    {
        await commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var wire = command.ToWireString();
            TaskCompletionSource<UvexFrame>? response = null;
            if (command.ExpectsResponse)
            {
                response = new(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (pendingGate)
                {
                    pendingCode = command.Code;
                    pendingWire = wire;
                    pendingEcho = true;
                    pending = response;
                }
            }

            try
            {
                await transport.WriteAsync(wire, cancellationToken).ConfigureAwait(false);
                if (response is null)
                {
                    return null;
                }

                var frame = await response.Task.WaitAsync(commandTimeout, cancellationToken).ConfigureAwait(false);
                if (frame.Code is "IERR" or "IALE")
                {
                    throw new UvexProtocolException(frame.Code, $"UVEX returned {frame.Code}: {string.Join(';', frame.Arguments)}");
                }

                return frame;
            }
            finally
            {
                lock (pendingGate)
                {
                    pending = null;
                    pendingCode = null;
                    pendingWire = null;
                    pendingEcho = false;
                }
            }
        }
        finally
        {
            commandGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync(CancellationToken.None).ConfigureAwait(false);
        commandGate.Dispose();
        readCts?.Dispose();
        readCts = null;
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        await foreach (var chunk in transport.ReadChunksAsync(cancellationToken).ConfigureAwait(false))
        {
            foreach (var frame in parser.Append(chunk))
            {
                TaskCompletionSource<UvexFrame>? completion = null;
                var isEcho = false;
                lock (pendingGate)
                {
                    if (pendingEcho && string.Equals(pendingWire, frame.Raw, StringComparison.OrdinalIgnoreCase))
                    {
                        pendingEcho = false;
                        isEcho = true;
                    }
                    else if (pendingCode == frame.Code || frame.Code is "IERR" or "IALE")
                    {
                        completion = pending;
                    }
                }

                if (isEcho)
                {
                    continue;
                }

                if (completion is not null)
                {
                    completion.TrySetResult(frame);
                }
                else
                {
                    UnsolicitedFrame?.Invoke(this, frame);
                }
            }
        }
    }
}
