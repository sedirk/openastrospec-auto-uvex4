using NINA.Core.Utility;
using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

// This only fans out a signal. Existing owner join/readback/ledger boundaries
// independently decide whether cleanup, handover or closeout is permitted.
internal static class NativeCancellation
{
    internal static void Request(CancellationTokenSource? source)
    {
        if (CancellationSignal.Request(source) is not { } failure) return;
        Report(failure);
    }

    internal static void Report(AggregateException failure)
    {
        try
        {
            Logger.Error(new InvalidOperationException(
                "CANCEL_CALLBACK_FAILED: cancellation was signalled, but a native callback failed; device stop remains unconfirmed.", failure));
        }
        catch (Exception loggingFailure)
        {
            // Diagnostic I/O (for example a full disk during shutdown) cannot
            // prevent cancellation of the next device owner either.
            System.Diagnostics.Trace.TraceError("CANCEL_CALLBACK_FAILED: {0}; logging failed: {1}", failure, loggingFailure);
        }
    }
}
