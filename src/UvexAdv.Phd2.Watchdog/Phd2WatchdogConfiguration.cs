using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UvexAdv.Phd2.Watchdog;

public sealed record Phd2WatchdogOptions
{
    public const int CurrentSchemaVersion = 1;
    public const string RequiredPhd2Host = "127.0.0.1";
    public const int RequiredPhd2Port = 4400;
    public const string RequiredLeaseFileName = "lease.json";
    public const string RequiredStatusFileName = "status.json";

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public string Phd2Host { get; init; } = RequiredPhd2Host;

    public int Phd2Port { get; init; } = RequiredPhd2Port;

    public int PollIntervalMilliseconds { get; init; } = 500;

    public int StopFailureRetryMilliseconds { get; init; } = 5_000;

    public int StatusPublishIntervalMilliseconds { get; init; } = 30_000;

    public string LeaseFileName { get; init; } = RequiredLeaseFileName;

    public string StatusFileName { get; init; } = RequiredStatusFileName;

    public TimeSpan PollInterval => TimeSpan.FromMilliseconds(PollIntervalMilliseconds);

    public TimeSpan StopFailureRetryInterval => TimeSpan.FromMilliseconds(StopFailureRetryMilliseconds);

    public TimeSpan StatusPublishInterval => TimeSpan.FromMilliseconds(StatusPublishIntervalMilliseconds);

    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException($"Unsupported PHD2 watchdog configuration schema {SchemaVersion}.");
        }
        if (!string.Equals(Phd2Host, RequiredPhd2Host, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"PHD2 watchdog endpoint is pinned to {RequiredPhd2Host}; '{Phd2Host}' is forbidden.");
        }
        if (Phd2Port != RequiredPhd2Port)
        {
            throw new InvalidDataException(
                $"PHD2 watchdog endpoint is pinned to port {RequiredPhd2Port}; port {Phd2Port} is forbidden.");
        }
        if (PollIntervalMilliseconds is < 100 or > 10_000)
        {
            throw new InvalidDataException("pollIntervalMilliseconds must be between 100 and 10000.");
        }
        if (StopFailureRetryMilliseconds is < 1_000 or > 60_000)
        {
            throw new InvalidDataException("stopFailureRetryMilliseconds must be between 1000 and 60000.");
        }
        if (StatusPublishIntervalMilliseconds is < 1_000 or > 300_000)
        {
            throw new InvalidDataException(
                "statusPublishIntervalMilliseconds must be between 1000 and 300000.");
        }
        if (!string.Equals(LeaseFileName, RequiredLeaseFileName, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"leaseFileName is pinned to '{RequiredLeaseFileName}'.");
        }
        if (!string.Equals(StatusFileName, RequiredStatusFileName, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"statusFileName is pinned to '{RequiredStatusFileName}'.");
        }
    }
}

public sealed record LoadedPhd2WatchdogConfiguration(
    Phd2WatchdogOptions Options,
    string Sha256);

public sealed record Phd2WatchdogPaths(
    string DataRoot,
    string ConfigurationPath,
    string LeasePath,
    string StatusPath)
{
    public const string ProductDirectoryName = "UVEX-ADV";
    public const string WatchdogDirectoryName = "phd2-safety";
    public const string ConfigurationFileName = "config.json";

    public static string GetMachineDataRoot()
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (string.IsNullOrWhiteSpace(programData))
        {
            throw new InvalidOperationException("Windows common application-data directory is unavailable.");
        }
        return Path.GetFullPath(Path.Combine(programData, ProductDirectoryName, WatchdogDirectoryName));
    }

    public static Phd2WatchdogPaths ForMachine(Phd2WatchdogOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        return FromDataRoot(GetMachineDataRoot(), options);
    }

    public static Phd2WatchdogPaths FromDataRoot(string dataRoot, Phd2WatchdogOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var root = Path.GetFullPath(dataRoot);
        var configuration = ResolveContainedPath(root, ConfigurationFileName);
        var lease = ResolveContainedPath(root, options.LeaseFileName);
        var status = ResolveContainedPath(root, options.StatusFileName);
        return new(root, configuration, lease, status);
    }

    private static string ResolveContainedPath(string root, string fileName)
    {
        var resolved = Path.GetFullPath(Path.Combine(root, fileName));
        if (!resolved.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Resolved watchdog path escapes the ProgramData root: {resolved}");
        }
        return resolved;
    }
}

public static class Phd2WatchdogConfigurationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static LoadedPhd2WatchdogConfiguration Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var bytes = File.ReadAllBytes(path);
        var options = JsonSerializer.Deserialize<Phd2WatchdogOptions>(bytes, JsonOptions)
            ?? throw new InvalidDataException("PHD2 watchdog configuration is empty.");
        options.Validate();
        return new(options, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    public static void WriteAtomic(string path, Phd2WatchdogOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
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
                JsonSerializer.Serialize(stream, options, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); } catch (IOException) { }
        }
    }
}
