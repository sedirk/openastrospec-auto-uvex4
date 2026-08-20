using System.Text.Json;
using System.Text.Json.Serialization;
using UvexAdv.Phd2;

namespace UvexAdv.Phd2.Watchdog;

public enum Phd2WatchdogHealth
{
    Healthy,
    Degraded,
    Unhealthy,
    Stopping,
}

public sealed record Phd2WatchdogStatus(
    int SchemaVersion,
    string ServiceName,
    int ProcessId,
    DateTimeOffset StartedUtc,
    DateTimeOffset UpdatedUtc,
    long Cycle,
    Phd2WatchdogHealth Health,
    string Code,
    string Message,
    string PinnedPhd2Endpoint,
    string ConfigurationSha256,
    string LeasePath,
    Guid? LeaseId,
    long? LeaseRevision,
    Phd2SafetyLeaseState? LeaseState,
    DateTimeOffset? LeaseExpiresUtc,
    bool StopAttemptedThisCycle,
    bool StopConfirmedThisCycle)
{
    public const int CurrentSchemaVersion = 1;
    public const string WindowsServiceName = "UVEX-ADV-PHD2-WATCHDOG";
}

public interface IPhd2WatchdogStatusStore
{
    Task WriteAsync(Phd2WatchdogStatus status, CancellationToken cancellationToken);
}

public sealed class AtomicPhd2WatchdogStatusStore : IPhd2WatchdogStatusStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string path;

    public AtomicPhd2WatchdogStatusStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        this.path = Path.GetFullPath(path);
    }

    public async Task WriteAsync(Phd2WatchdogStatus status, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(status);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, status, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); } catch (IOException) { }
        }
    }

    public static Phd2WatchdogStatus Read(string path)
    {
        var status = JsonSerializer.Deserialize<Phd2WatchdogStatus>(File.ReadAllBytes(path), JsonOptions)
            ?? throw new InvalidDataException("PHD2 watchdog status is empty.");
        if (status.SchemaVersion != Phd2WatchdogStatus.CurrentSchemaVersion)
        {
            throw new InvalidDataException($"Unsupported PHD2 watchdog status schema {status.SchemaVersion}.");
        }
        return status;
    }
}
