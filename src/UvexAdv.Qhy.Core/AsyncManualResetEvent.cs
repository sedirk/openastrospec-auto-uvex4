namespace UvexAdv.Qhy.Core;

internal sealed class AsyncManualResetEvent(bool initialState)
{
    private volatile TaskCompletionSource<bool> source = CreateSource(initialState);

    public Task WaitAsync(CancellationToken cancellationToken) => source.Task.WaitAsync(cancellationToken);

    public void Set() => source.TrySetResult(true);

    public void Reset()
    {
        while (true)
        {
            var current = source;
            if (!current.Task.IsCompleted) return;
            var replacement = CreateSource(false);
            if (ReferenceEquals(Interlocked.CompareExchange(ref source, replacement, current), current)) return;
        }
    }

    private static TaskCompletionSource<bool> CreateSource(bool completed)
    {
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (completed) result.SetResult(true);
        return result;
    }
}
