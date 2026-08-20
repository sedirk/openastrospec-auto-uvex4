using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UvexAdv.Phd2;

/// <summary>
/// Process-independent lease protocol for a dedicated PHD2 safety-watchdog
/// process. The observation process only renews the file. A different process
/// must run <see cref="Phd2SafetyWatchdog"/> so its lifetime does not end with
/// N.I.N.A. or the plugin.
/// </summary>
public enum Phd2SafetyLeaseState
{
    Active,
    StopPending,
    StopConfirmed,
    StopFailed,
    Released,
}

public sealed record Phd2SafetyLease(
    int SchemaVersion,
    Guid LeaseId,
    string OwnerInstanceId,
    int OwnerProcessId,
    string Phd2Host,
    int Phd2Port,
    DateTimeOffset IssuedUtc,
    DateTimeOffset ExpiresUtc,
    long Revision,
    Phd2SafetyLeaseState State,
    DateTimeOffset? StopAttemptedUtc = null,
    DateTimeOffset? StopConfirmedUtc = null,
    string? LastError = null)
{
    public const int CurrentSchemaVersion = 1;

    public IReadOnlyList<string> Validate()
    {
        var issues = new List<string>();
        if (SchemaVersion != CurrentSchemaVersion) issues.Add($"Unsupported schema version {SchemaVersion}.");
        if (LeaseId == Guid.Empty) issues.Add("LeaseId is empty.");
        if (string.IsNullOrWhiteSpace(OwnerInstanceId)) issues.Add("OwnerInstanceId is empty.");
        if (OwnerProcessId <= 0) issues.Add("OwnerProcessId must be positive.");
        if (!IsLoopbackHost(Phd2Host)) issues.Add("PHD2 safety leases must use a loopback host.");
        if (Phd2Port is < 1 or > 65535) issues.Add("PHD2 port must be between 1 and 65535.");
        if (IssuedUtc == default || ExpiresUtc <= IssuedUtc) issues.Add("Lease timestamps are invalid.");
        if (ExpiresUtc - IssuedUtc > TimeSpan.FromMinutes(5)) issues.Add("A safety lease cannot exceed five minutes.");
        if (Revision < 1) issues.Add("Revision must be positive.");
        return issues;
    }

    private static bool IsLoopbackHost(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));
}

public sealed class Phd2SafetyLeaseStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string lockPath;

    public Phd2SafetyLeaseStore(string leasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leasePath);
        if (!Path.IsPathFullyQualified(leasePath))
        {
            throw new ArgumentException("PHD2 safety-lease path must be absolute.", nameof(leasePath));
        }
        LeasePath = Path.GetFullPath(leasePath);
        lockPath = LeasePath + ".lock";
    }

    public string LeasePath { get; }

    public async Task<Phd2SafetyLease?> ReadAsync(CancellationToken cancellationToken)
    {
        await using var leaseLock = await AcquireLockAsync(cancellationToken).ConfigureAwait(false);
        return ReadLocked();
    }

    public async Task<bool> TryAcquireAsync(
        Phd2SafetyLease lease,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        ValidateLease(lease);
        if (lease.State != Phd2SafetyLeaseState.Active)
        {
            throw new ArgumentException("A newly acquired safety lease must be Active.", nameof(lease));
        }
        if (lease.ExpiresUtc <= nowUtc.ToUniversalTime())
        {
            throw new ArgumentException("A newly acquired safety lease must not already be expired.", nameof(lease));
        }
        await using var leaseLock = await AcquireLockAsync(cancellationToken).ConfigureAwait(false);
        var current = ReadLocked();
        if (current is not null &&
            current.State is not (Phd2SafetyLeaseState.Released or Phd2SafetyLeaseState.StopConfirmed))
        {
            // Even an expired Active lease must first be consumed by the
            // watchdog. A restarted owner cannot erase evidence of a crash.
            return false;
        }
        WriteLocked(lease);
        return true;
    }

    public async Task<bool> TryReplaceAsync(
        Guid expectedLeaseId,
        long expectedRevision,
        Phd2SafetyLease replacement,
        CancellationToken cancellationToken)
    {
        ValidateLease(replacement);
        if (replacement.LeaseId != expectedLeaseId ||
            replacement.Revision != expectedRevision + 1)
        {
            throw new ArgumentException("Replacement must preserve LeaseId and increment Revision exactly once.");
        }
        await using var leaseLock = await AcquireLockAsync(cancellationToken).ConfigureAwait(false);
        var current = ReadLocked();
        if (current?.LeaseId != expectedLeaseId || current.Revision != expectedRevision)
        {
            return false;
        }
        WriteLocked(replacement);
        return true;
    }

    private Phd2SafetyLease? ReadLocked()
    {
        if (!File.Exists(LeasePath)) return null;
        var lease = JsonSerializer.Deserialize<Phd2SafetyLease>(
            File.ReadAllBytes(LeasePath),
            JsonOptions) ?? throw new InvalidDataException("PHD2 safety-lease JSON is empty.");
        ValidateLease(lease);
        return lease;
    }

    private void WriteLocked(Phd2SafetyLease lease)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LeasePath)!);
        var temporary = LeasePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, lease, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, LeasePath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); } catch (IOException) { }
        }
    }

    private async Task<FileStream> AcquireLockAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LeasePath)!);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.DeleteOnClose);
            }
            catch (IOException)
            {
                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static void ValidateLease(Phd2SafetyLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        var issues = lease.Validate();
        if (issues.Count > 0)
        {
            throw new InvalidDataException(string.Join(" ", issues));
        }
    }
}

public sealed class Phd2SafetyLeaseHeartbeat : IAsyncDisposable
{
    private readonly Phd2SafetyLeaseStore store;
    private readonly string host;
    private readonly int port;
    private readonly TimeSpan leaseDuration;
    private readonly TimeSpan heartbeatInterval;
    private readonly CancellationTokenSource lifetime = new();
    private Task? loop;
    private Phd2SafetyLease? current;
    private int disposeState;

    public Phd2SafetyLeaseHeartbeat(
        Phd2SafetyLeaseStore store,
        string host,
        int port,
        TimeSpan leaseDuration,
        TimeSpan heartbeatInterval,
        string? ownerInstanceId = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.host = host;
        this.port = port;
        this.leaseDuration = leaseDuration;
        this.heartbeatInterval = heartbeatInterval;
        OwnerInstanceId = string.IsNullOrWhiteSpace(ownerInstanceId)
            ? Guid.NewGuid().ToString("N")
            : ownerInstanceId;
        if (leaseDuration <= TimeSpan.Zero || leaseDuration > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        }
        if (heartbeatInterval <= TimeSpan.Zero || heartbeatInterval >= leaseDuration)
        {
            throw new ArgumentOutOfRangeException(nameof(heartbeatInterval));
        }
    }

    public string OwnerInstanceId { get; }

    public Exception? Failure { get; private set; }

    public Phd2SafetyLease? Current => current;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposeState) != 0, this);
        if (loop is not null) throw new InvalidOperationException("PHD2 safety heartbeat is already started.");
        var now = DateTimeOffset.UtcNow;
        var lease = new Phd2SafetyLease(
            Phd2SafetyLease.CurrentSchemaVersion,
            Guid.NewGuid(),
            OwnerInstanceId,
            Environment.ProcessId,
            host,
            port,
            now,
            now + leaseDuration,
            Revision: 1,
            Phd2SafetyLeaseState.Active);
        if (!await store.TryAcquireAsync(lease, now, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "An unreconciled PHD2 safety lease already exists; the independent watchdog must consume it first.");
        }
        current = lease;
        loop = RunAsync(lifetime.Token);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposeState, 1) != 0) return;
        lifetime.Cancel();
        if (loop is not null)
        {
            try { await loop.ConfigureAwait(false); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        }
        var lease = current;
        if (lease?.State == Phd2SafetyLeaseState.Active)
        {
            var released = lease with
            {
                Revision = lease.Revision + 1,
                State = Phd2SafetyLeaseState.Released,
                ExpiresUtc = DateTimeOffset.UtcNow > lease.IssuedUtc
                    ? DateTimeOffset.UtcNow
                    : lease.ExpiresUtc,
            };
            try
            {
                var releasedOnDisk = await store.TryReplaceAsync(
                    lease.LeaseId,
                    lease.Revision,
                    released,
                    CancellationToken.None).ConfigureAwait(false);
                if (releasedOnDisk) current = released;
            }
            catch { }
        }
        lifetime.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(heartbeatInterval, cancellationToken).ConfigureAwait(false);
                var lease = current ?? throw new InvalidOperationException("Safety lease disappeared.");
                var now = DateTimeOffset.UtcNow;
                if (now >= lease.ExpiresUtc)
                {
                    throw new TimeoutException("PHD2 safety heartbeat missed its own lease expiry.");
                }
                var renewed = lease with
                {
                    Revision = lease.Revision + 1,
                    IssuedUtc = now,
                    ExpiresUtc = now + leaseDuration,
                };
                if (!await store.TryReplaceAsync(
                    lease.LeaseId,
                    lease.Revision,
                    renewed,
                    cancellationToken).ConfigureAwait(false))
                {
                    throw new InvalidOperationException("PHD2 safety lease was claimed or replaced by the watchdog.");
                }
                current = renewed;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Failure = ex;
        }
    }
}

public interface IPhd2SafetyStopper
{
    Task<Phd2StopCaptureResult> StopCaptureAndConfirmAsync(
        string host,
        int port,
        CancellationToken cancellationToken);
}

public sealed class Phd2ClientSafetyStopper : IPhd2SafetyStopper
{
    public async Task<Phd2StopCaptureResult> StopCaptureAndConfirmAsync(
        string host,
        int port,
        CancellationToken cancellationToken)
    {
        await using var client = new Phd2Client(new Phd2ClientOptions
        {
            Host = host,
            Port = port,
            AllowNonLoopbackEndpoint = false,
        });
        await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        return await client.StopCaptureAndConfirmAsync(cancellationToken).ConfigureAwait(false);
    }
}

public sealed record Phd2SafetyWatchdogResult(
    Phd2SafetyLease? Lease,
    bool StopAttempted,
    bool StopConfirmed,
    string Code,
    string Message);

public sealed class Phd2SafetyWatchdog
{
    private readonly Phd2SafetyLeaseStore store;
    private readonly IPhd2SafetyStopper stopper;

    public Phd2SafetyWatchdog(Phd2SafetyLeaseStore store, IPhd2SafetyStopper stopper)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.stopper = stopper ?? throw new ArgumentNullException(nameof(stopper));
    }

    /// <summary>
    /// One poll for an independent Windows-service loop. Active, unexpired
    /// leases are left alone. Expired, pending, or previously failed leases
    /// invoke idempotent stop_capture and require a confirmed Stopped/Selected
    /// state before the lease becomes StopConfirmed.
    /// </summary>
    public async Task<Phd2SafetyWatchdogResult> EvaluateOnceAsync(
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var lease = await store.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (lease is null)
        {
            return new(null, false, false, "LEASE_MISSING", "No PHD2 safety lease exists.");
        }
        if (lease.State is Phd2SafetyLeaseState.Released or Phd2SafetyLeaseState.StopConfirmed)
        {
            return new(lease, false, lease.State == Phd2SafetyLeaseState.StopConfirmed, "LEASE_TERMINAL", $"Lease is {lease.State}.");
        }
        if (lease.State == Phd2SafetyLeaseState.Active && nowUtc < lease.ExpiresUtc)
        {
            return new(lease, false, false, "LEASE_HEALTHY", $"Lease is valid until {lease.ExpiresUtc:O}.");
        }

        if (lease.State == Phd2SafetyLeaseState.Active)
        {
            var pending = lease with
            {
                Revision = lease.Revision + 1,
                State = Phd2SafetyLeaseState.StopPending,
                StopAttemptedUtc = nowUtc,
                LastError = null,
            };
            if (!await store.TryReplaceAsync(
                lease.LeaseId,
                lease.Revision,
                pending,
                cancellationToken).ConfigureAwait(false))
            {
                return new(lease, false, false, "LEASE_RACE_RETRY", "Lease changed while the watchdog claimed it; retry the poll.");
            }
            lease = pending;
        }

        try
        {
            var stopped = await stopper.StopCaptureAndConfirmAsync(
                lease.Phd2Host,
                lease.Phd2Port,
                cancellationToken).ConfigureAwait(false);
            if (!stopped.ConfirmedIdle ||
                stopped.FinalState is not (Phd2AppState.Stopped or Phd2AppState.Selected))
            {
                throw new Phd2Exception($"Stopper returned unconfirmed state {stopped.FinalState}.");
            }
            var confirmed = lease with
            {
                Revision = lease.Revision + 1,
                State = Phd2SafetyLeaseState.StopConfirmed,
                StopConfirmedUtc = DateTimeOffset.UtcNow,
                LastError = null,
            };
            _ = await store.TryReplaceAsync(
                lease.LeaseId,
                lease.Revision,
                confirmed,
                cancellationToken).ConfigureAwait(false);
            return new(confirmed, true, true, "EXPIRED_LEASE_STOP_CONFIRMED", $"PHD2 is confirmed {stopped.FinalState}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var failed = lease with
            {
                Revision = lease.Revision + 1,
                State = Phd2SafetyLeaseState.StopFailed,
                StopAttemptedUtc = DateTimeOffset.UtcNow,
                LastError = ex.Message,
            };
            _ = await store.TryReplaceAsync(
                lease.LeaseId,
                lease.Revision,
                failed,
                CancellationToken.None).ConfigureAwait(false);
            return new(failed, true, false, "EXPIRED_LEASE_STOP_FAILED", ex.Message);
        }
    }
}
