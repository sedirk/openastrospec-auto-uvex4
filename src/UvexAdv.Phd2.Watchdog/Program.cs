using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using UvexAdv.Phd2;
using UvexAdv.Phd2.Watchdog;

var commandExitCode = TryRunReadOnlyCommand(args);
if (commandExitCode is not null)
{
    return commandExitCode.Value;
}

var dataRoot = Phd2WatchdogPaths.GetMachineDataRoot();
LoadedPhd2WatchdogConfiguration configuration;
Phd2WatchdogPaths paths;
try
{
    var configurationPath = Path.Combine(dataRoot, Phd2WatchdogPaths.ConfigurationFileName);
    configuration = Phd2WatchdogConfigurationStore.Load(configurationPath);
    paths = Phd2WatchdogPaths.ForMachine(configuration.Options);
}
catch (Exception ex)
{
    await TryWriteBootstrapFailureStatusAsync(dataRoot, ex).ConfigureAwait(false);
    Console.Error.WriteLine(ex.Message);
    return 1;
}

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options =>
    options.ServiceName = Phd2WatchdogStatus.WindowsServiceName);
builder.Services.AddSingleton(configuration);
builder.Services.AddSingleton(configuration.Options);
builder.Services.AddSingleton(paths);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(new Phd2SafetyLeaseStore(paths.LeasePath));
builder.Services.AddSingleton<IPhd2WatchdogStatusStore>(
    new AtomicPhd2WatchdogStatusStore(paths.StatusPath));
builder.Services.AddSingleton<IPhd2SafetyStopper>(_ =>
    new PinnedPhd2SafetyStopper(
        new BoundedPhd2SafetyStopper(
            new Phd2ClientSafetyStopper(),
            TimeSpan.FromSeconds(30))));
builder.Services.AddSingleton<Phd2SafetyWatchdog>();
builder.Services.AddSingleton<Phd2WatchdogCycle>();
builder.Services.AddHostedService<Phd2WatchdogWorker>();

await builder.Build().RunAsync().ConfigureAwait(false);
return 0;

static int? TryRunReadOnlyCommand(string[] arguments)
{
    if (arguments.Length == 0)
    {
        return null;
    }
    if (arguments.Length != 1)
    {
        Console.Error.WriteLine("Supported commands are --validate-config and --status.");
        return 64;
    }

    try
    {
        var root = Phd2WatchdogPaths.GetMachineDataRoot();
        var configPath = Path.Combine(root, Phd2WatchdogPaths.ConfigurationFileName);
        if (string.Equals(arguments[0], "--validate-config", StringComparison.OrdinalIgnoreCase))
        {
            var loaded = Phd2WatchdogConfigurationStore.Load(configPath);
            var paths = Phd2WatchdogPaths.ForMachine(loaded.Options);
            Console.WriteLine($"Configuration valid: {loaded.Sha256}");
            Console.WriteLine($"Pinned PHD2 endpoint: {loaded.Options.Phd2Host}:{loaded.Options.Phd2Port}");
            Console.WriteLine($"Lease: {paths.LeasePath}");
            return 0;
        }
        if (string.Equals(arguments[0], "--status", StringComparison.OrdinalIgnoreCase))
        {
            var statusPath = Path.Combine(root, "status.json");
            try
            {
                var loaded = Phd2WatchdogConfigurationStore.Load(configPath);
                statusPath = Phd2WatchdogPaths.ForMachine(loaded.Options).StatusPath;
            }
            catch (Exception)
            {
                // A configuration bootstrap failure is itself published to the
                // fixed default status path so operators can still diagnose it.
            }
            var status = AtomicPhd2WatchdogStatusStore.Read(statusPath);
            Console.WriteLine(File.ReadAllText(statusPath));
            return status.Health == Phd2WatchdogHealth.Unhealthy ? 2 : 0;
        }

        Console.Error.WriteLine("Supported commands are --validate-config and --status.");
        return 64;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
}

static async Task TryWriteBootstrapFailureStatusAsync(string dataRoot, Exception exception)
{
    try
    {
        var now = DateTimeOffset.UtcNow;
        var statusPath = Path.Combine(dataRoot, "status.json");
        var leasePath = Path.Combine(dataRoot, "lease.json");
        var status = new Phd2WatchdogStatus(
            Phd2WatchdogStatus.CurrentSchemaVersion,
            Phd2WatchdogStatus.WindowsServiceName,
            Environment.ProcessId,
            now,
            now,
            Cycle: 0,
            Phd2WatchdogHealth.Unhealthy,
            "CONFIGURATION_INVALID",
            exception.Message,
            $"{Phd2WatchdogOptions.RequiredPhd2Host}:{Phd2WatchdogOptions.RequiredPhd2Port}",
            ConfigurationSha256: "UNAVAILABLE",
            leasePath,
            LeaseId: null,
            LeaseRevision: null,
            LeaseState: null,
            LeaseExpiresUtc: null,
            StopAttemptedThisCycle: false,
            StopConfirmedThisCycle: false);
        await new AtomicPhd2WatchdogStatusStore(statusPath)
            .WriteAsync(status, CancellationToken.None)
            .ConfigureAwait(false);
    }
    catch
    {
        // The original configuration error remains the process exit reason.
    }
}

public partial class Program;
