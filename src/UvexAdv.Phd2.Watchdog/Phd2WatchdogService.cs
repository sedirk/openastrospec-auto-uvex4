using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UvexAdv.Phd2;

namespace UvexAdv.Phd2.Watchdog;

/// <summary>
/// Defense in depth around the library stopper: even a malformed or manually
/// planted lease cannot make the Windows service connect anywhere except the
/// one local PHD2 event-server endpoint.
/// </summary>
public sealed class PinnedPhd2SafetyStopper : IPhd2SafetyStopper
{
    private readonly IPhd2SafetyStopper inner;

    public PinnedPhd2SafetyStopper(IPhd2SafetyStopper inner)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public Task<Phd2StopCaptureResult> StopCaptureAndConfirmAsync(
        string host,
        int port,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(host, Phd2WatchdogOptions.RequiredPhd2Host, StringComparison.Ordinal) ||
            port != Phd2WatchdogOptions.RequiredPhd2Port)
        {
            throw new InvalidOperationException(
                $"Watchdog refused non-pinned PHD2 endpoint '{host}:{port}'.");
        }
        return inner.StopCaptureAndConfirmAsync(host, port, cancellationToken);
    }
}

public sealed class BoundedPhd2SafetyStopper : IPhd2SafetyStopper
{
    private readonly IPhd2SafetyStopper inner;
    private readonly TimeSpan timeout;

    public BoundedPhd2SafetyStopper(IPhd2SafetyStopper inner, TimeSpan timeout)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }
        this.timeout = timeout;
    }

    public async Task<Phd2StopCaptureResult> StopCaptureAndConfirmAsync(
        string host,
        int port,
        CancellationToken cancellationToken)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(timeout);
        try
        {
            return await inner.StopCaptureAndConfirmAsync(host, port, bounded.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"PHD2 safety stop exceeded {timeout}.", ex);
        }
    }
}

public sealed class Phd2WatchdogCycle
{
    private readonly Phd2SafetyWatchdog watchdog;
    private readonly Phd2SafetyLeaseStore leaseStore;
    private readonly IPhd2WatchdogStatusStore statusStore;
    private readonly Phd2WatchdogPaths paths;
    private readonly LoadedPhd2WatchdogConfiguration configuration;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<Phd2WatchdogCycle> logger;
    private readonly DateTimeOffset startedUtc;
    private long cycle;
    private string? lastPublishedSignature;
    private DateTimeOffset? lastPublishedUtc;

    public Phd2WatchdogCycle(
        Phd2SafetyWatchdog watchdog,
        Phd2SafetyLeaseStore leaseStore,
        IPhd2WatchdogStatusStore statusStore,
        Phd2WatchdogPaths paths,
        LoadedPhd2WatchdogConfiguration configuration,
        TimeProvider timeProvider,
        ILogger<Phd2WatchdogCycle> logger)
    {
        this.watchdog = watchdog ?? throw new ArgumentNullException(nameof(watchdog));
        this.leaseStore = leaseStore ?? throw new ArgumentNullException(nameof(leaseStore));
        this.statusStore = statusStore ?? throw new ArgumentNullException(nameof(statusStore));
        this.paths = paths ?? throw new ArgumentNullException(nameof(paths));
        this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        startedUtc = timeProvider.GetUtcNow();
    }

    public async Task<Phd2WatchdogStatus> EvaluateOnceAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var currentCycle = Interlocked.Increment(ref cycle);
        Phd2WatchdogStatus status;
        try
        {
            var previous = await leaseStore.ReadAsync(cancellationToken).ConfigureAwait(false);
            Phd2SafetyWatchdogResult result;
            if (previous is not null &&
                (!string.Equals(
                    previous.Phd2Host,
                    Phd2WatchdogOptions.RequiredPhd2Host,
                    StringComparison.Ordinal) ||
                 previous.Phd2Port != Phd2WatchdogOptions.RequiredPhd2Port))
            {
                result = new Phd2SafetyWatchdogResult(
                    previous,
                    StopAttempted: false,
                    StopConfirmed: false,
                    "LEASE_ENDPOINT_INVALID",
                    $"Lease endpoint '{previous.Phd2Host}:{previous.Phd2Port}' is not the pinned local PHD2 endpoint.");
            }
            else if (previous is
            {
                State: Phd2SafetyLeaseState.StopFailed,
                StopAttemptedUtc: { } attemptedUtc,
            } &&
                now - attemptedUtc < configuration.Options.StopFailureRetryInterval)
            {
                var retryUtc = attemptedUtc + configuration.Options.StopFailureRetryInterval;
                result = new Phd2SafetyWatchdogResult(
                    previous,
                    StopAttempted: false,
                    StopConfirmed: false,
                    "STOP_RETRY_BACKOFF",
                    $"Previous stop failed; retry is deferred until {retryUtc:O}.");
            }
            else
            {
                result = await watchdog.EvaluateOnceAsync(now, cancellationToken).ConfigureAwait(false);
            }
            var health = result.Code switch
            {
                "EXPIRED_LEASE_STOP_FAILED" => Phd2WatchdogHealth.Unhealthy,
                "STOP_RETRY_BACKOFF" => Phd2WatchdogHealth.Unhealthy,
                "LEASE_ENDPOINT_INVALID" => Phd2WatchdogHealth.Unhealthy,
                "LEASE_RACE_RETRY" => Phd2WatchdogHealth.Degraded,
                _ => Phd2WatchdogHealth.Healthy,
            };
            status = BuildStatus(
                currentCycle,
                now,
                health,
                result.Code,
                result.Message,
                result.Lease,
                result.StopAttempted,
                result.StopConfirmed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            status = BuildStatus(
                currentCycle,
                now,
                Phd2WatchdogHealth.Unhealthy,
                "WATCHDOG_EVALUATION_FAILED",
                ex.Message,
                lease: null,
                stopAttempted: false,
                stopConfirmed: false);
        }

        var published = await PublishStatusAsync(status, force: false, cancellationToken).ConfigureAwait(false);
        if (published && status.Health == Phd2WatchdogHealth.Unhealthy)
        {
            logger.LogError("PHD2 watchdog unhealthy: {Code} {Message}", status.Code, status.Message);
        }
        else if (published && status.StopAttemptedThisCycle)
        {
            logger.LogWarning("PHD2 watchdog safety stop result: {Code} {Message}", status.Code, status.Message);
        }
        return status;
    }

    public async Task WriteStoppingStatusAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        _ = await PublishStatusAsync(
            BuildStatus(
                Interlocked.Read(ref cycle),
                now,
                Phd2WatchdogHealth.Stopping,
                "SERVICE_STOPPING",
                "The independent PHD2 watchdog service is stopping.",
                lease: null,
                stopAttempted: false,
                stopConfirmed: false),
            force: true,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> PublishStatusAsync(
        Phd2WatchdogStatus status,
        bool force,
        CancellationToken cancellationToken)
    {
        var signature = string.Join(
            '|',
            status.Health,
            status.Code,
            status.Message,
            status.LeaseId,
            status.LeaseRevision,
            status.LeaseState,
            status.LeaseExpiresUtc,
            status.StopAttemptedThisCycle,
            status.StopConfirmedThisCycle);
        if (!force &&
            string.Equals(signature, lastPublishedSignature, StringComparison.Ordinal) &&
            lastPublishedUtc is { } previous &&
            status.UpdatedUtc - previous < configuration.Options.StatusPublishInterval)
        {
            return false;
        }

        await statusStore.WriteAsync(status, cancellationToken).ConfigureAwait(false);
        lastPublishedSignature = signature;
        lastPublishedUtc = status.UpdatedUtc;
        return true;
    }

    private Phd2WatchdogStatus BuildStatus(
        long currentCycle,
        DateTimeOffset updatedUtc,
        Phd2WatchdogHealth health,
        string code,
        string message,
        Phd2SafetyLease? lease,
        bool stopAttempted,
        bool stopConfirmed) =>
        new(
            Phd2WatchdogStatus.CurrentSchemaVersion,
            Phd2WatchdogStatus.WindowsServiceName,
            Environment.ProcessId,
            startedUtc,
            updatedUtc,
            currentCycle,
            health,
            code,
            message,
            $"{Phd2WatchdogOptions.RequiredPhd2Host}:{Phd2WatchdogOptions.RequiredPhd2Port}",
            configuration.Sha256,
            paths.LeasePath,
            lease?.LeaseId,
            lease?.Revision,
            lease?.State,
            lease?.ExpiresUtc,
            stopAttempted,
            stopConfirmed);
}

public sealed class Phd2WatchdogWorker : BackgroundService
{
    private readonly Phd2WatchdogCycle cycle;
    private readonly Phd2WatchdogOptions options;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<Phd2WatchdogWorker> logger;

    public Phd2WatchdogWorker(
        Phd2WatchdogCycle cycle,
        Phd2WatchdogOptions options,
        TimeProvider timeProvider,
        ILogger<Phd2WatchdogWorker> logger)
    {
        this.cycle = cycle ?? throw new ArgumentNullException(nameof(cycle));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Independent PHD2 watchdog started; endpoint is pinned to {Host}:{Port}.",
            Phd2WatchdogOptions.RequiredPhd2Host,
            Phd2WatchdogOptions.RequiredPhd2Port);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    _ = await cycle.EvaluateOnceAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // A status-file I/O failure must not terminate the watchdog
                    // process. The next cycle retries both evaluation and status.
                    logger.LogError(ex, "PHD2 watchdog service cycle failed.");
                }

                await Task.Delay(options.PollInterval, timeProvider, stoppingToken).ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                await cycle.WriteStoppingStatusAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not write the final PHD2 watchdog status.");
            }
        }
    }
}
