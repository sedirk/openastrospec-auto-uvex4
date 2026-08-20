using System.Collections.Concurrent;

namespace UvexAdv.Service.Operations;

using UvexAdv.Service.Persistence;

public enum UvexOperationState
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

public sealed record UvexOperation(
    Guid Id,
    string Kind,
    UvexOperationState State,
    DateTimeOffset StartedUtc,
    DateTimeOffset? CompletedUtc = null,
    string? Error = null);

public sealed class OperationRegistry(ILogger<OperationRegistry> logger, UvexDatabase database)
{
    private readonly ConcurrentDictionary<Guid, UvexOperation> operations = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> cancellations = new();

    public UvexOperation Start(string kind, Func<CancellationToken, Task> action)
    {
        var id = Guid.NewGuid();
        var initial = new UvexOperation(id, kind, UvexOperationState.Pending, DateTimeOffset.UtcNow);
        operations[id] = initial;
        database.UpsertOperation(initial);
        var cts = new CancellationTokenSource();
        cancellations[id] = cts;
        _ = RunAsync(initial, action, cts);
        return initial;
    }

    public UvexOperation? Get(Guid id) => operations.GetValueOrDefault(id);

    public IReadOnlyList<UvexOperation> Recent(int count = 50) =>
        operations.Values.OrderByDescending(static operation => operation.StartedUtc).Take(Math.Clamp(count, 1, 200)).ToArray();

    public bool Cancel(Guid id)
    {
        if (!cancellations.TryGetValue(id, out var cts))
        {
            return false;
        }

        cts.Cancel();
        return true;
    }

    private async Task RunAsync(UvexOperation initial, Func<CancellationToken, Task> action, CancellationTokenSource cts)
    {
        operations[initial.Id] = initial with { State = UvexOperationState.Running };
        database.UpsertOperation(operations[initial.Id]);
        try
        {
            await action(cts.Token).ConfigureAwait(false);
            operations[initial.Id] = initial with { State = UvexOperationState.Succeeded, CompletedUtc = DateTimeOffset.UtcNow };
            database.UpsertOperation(operations[initial.Id]);
        }
        catch (OperationCanceledException)
        {
            operations[initial.Id] = initial with { State = UvexOperationState.Cancelled, CompletedUtc = DateTimeOffset.UtcNow };
            database.UpsertOperation(operations[initial.Id]);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "UVEX operation {OperationId} ({Kind}) failed", initial.Id, initial.Kind);
            operations[initial.Id] = initial with { State = UvexOperationState.Failed, CompletedUtc = DateTimeOffset.UtcNow, Error = ex.Message };
            database.UpsertOperation(operations[initial.Id]);
        }
        finally
        {
            cancellations.TryRemove(initial.Id, out _);
            cts.Dispose();
        }
    }
}
