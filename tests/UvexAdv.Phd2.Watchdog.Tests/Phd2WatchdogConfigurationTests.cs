using UvexAdv.Phd2.Watchdog;

namespace UvexAdv.Phd2.Watchdog.Tests;

public sealed class Phd2WatchdogConfigurationTests
{
    [Fact]
    public void SafeDefaultsPinEndpointAndProgramDataLeafFiles()
    {
        var options = new Phd2WatchdogOptions();

        options.Validate();
        var paths = Phd2WatchdogPaths.FromDataRoot(
            Path.Combine(Path.GetTempPath(), "safe-root"),
            options);

        Assert.Equal("127.0.0.1", options.Phd2Host);
        Assert.Equal(4400, options.Phd2Port);
        Assert.Equal("lease.json", Path.GetFileName(paths.LeasePath));
        Assert.Equal("status.json", Path.GetFileName(paths.StatusPath));
        Assert.Equal(paths.DataRoot, Path.GetDirectoryName(paths.LeasePath));
        Assert.Equal(paths.DataRoot, Path.GetDirectoryName(paths.StatusPath));
    }

    [Theory]
    [InlineData("localhost", 4400)]
    [InlineData("::1", 4400)]
    [InlineData("127.0.0.1", 4401)]
    [InlineData("192.0.2.1", 4400)]
    public void ConfigurationRejectsEveryNonPinnedEndpoint(string host, int port)
    {
        var options = new Phd2WatchdogOptions
        {
            Phd2Host = host,
            Phd2Port = port,
        };

        Assert.Throws<InvalidDataException>(options.Validate);
    }

    [Theory]
    [InlineData("..\\lease.json")]
    [InlineData("subdirectory/lease.json")]
    [InlineData("C:\\lease.json")]
    public void ConfigurationRejectsLeasePathEscapes(string leaseFileName)
    {
        var options = new Phd2WatchdogOptions { LeaseFileName = leaseFileName };

        Assert.Throws<InvalidDataException>(options.Validate);
    }

    [Fact]
    public void ConfigurationReplacementIsAtomicAndReloadable()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "config.json");
        Phd2WatchdogConfigurationStore.WriteAtomic(path, new Phd2WatchdogOptions());
        var first = Phd2WatchdogConfigurationStore.Load(path);

        Phd2WatchdogConfigurationStore.WriteAtomic(
            path,
            first.Options with { PollIntervalMilliseconds = 750 });
        var replacement = Phd2WatchdogConfigurationStore.Load(path);

        Assert.Equal(750, replacement.Options.PollIntervalMilliseconds);
        Assert.NotEqual(first.Sha256, replacement.Sha256);
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.tmp"));
    }
}
