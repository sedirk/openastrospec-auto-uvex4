namespace UvexAdv.Observatory;

/// <summary>
/// Dispatches a cancellation signal to every registered callback. Native owner
/// callbacks can throw while a device is disconnecting; that must not prevent
/// other owners from receiving cancellation. A signal is never proof of stop.
/// </summary>
public static class CancellationSignal
{
    public static AggregateException? Request(CancellationTokenSource? source)
    {
        try { source?.Cancel(throwOnFirstException: false); }
        catch (ObjectDisposedException) { /* The joined scope has already retired. */ }
        catch (AggregateException failure) { return failure.Flatten(); }
        return null;
    }
}
