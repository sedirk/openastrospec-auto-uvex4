using UvexAdv.Core;
using UvexAdv.Service.Persistence;
using Microsoft.Extensions.Configuration;

namespace UvexAdv.Service.Tests;

public sealed class CalibrationProfilePolicyTests
{
    [Fact]
    public void PublishedServiceDefaultKeepsLegacyRealConfigAtThreeHundredLinesPerMillimeter()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Uvex:Simulator"] = "false",
            })
            .Build();
        var options = new UvexSafetyOptions();

        configuration.GetSection("Uvex").Bind(options);

        Assert.False(options.Simulator);
        Assert.Equal(300, options.ExpectedGratingLinesPerMm);
    }

    [Fact]
    public void ExplicitSimulatorConfigurationOverridesPublishedGratingDefault()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Uvex:Simulator"] = "true",
                ["Uvex:ExpectedGratingLinesPerMm"] = "600",
            })
            .Build();
        var options = new UvexSafetyOptions();

        configuration.GetSection("Uvex").Bind(options);

        Assert.True(options.Simulator);
        Assert.Equal(600, options.ExpectedGratingLinesPerMm);
    }

    [Fact]
    public void LegacySimulatorProfileIsNeverCompatibleWithRealHardware()
    {
        var profile = Profile("sim-default", 600, CalibrationProfileScope.Unspecified);
        var options = RealOptions();

        Assert.False(CalibrationProfilePolicy.IsCompatible(profile, options));
        Assert.Empty(CalibrationProfilePolicy.CompatibleProfiles([profile], options));
    }

    [Fact]
    public void RealProfileMustBeExplicitlyScopedBoundAndUseExpectedGrating()
    {
        var options = RealOptions();
        var prepared = CalibrationProfilePolicy.PrepareForStorage(
            Profile("uvex-300", 300, CalibrationProfileScope.Hardware),
            options);

        Assert.Equal(CalibrationProfilePolicy.ExpectedHardwareBinding(options), prepared.HardwareBinding);
        Assert.True(CalibrationProfilePolicy.IsCompatible(prepared, options));
        Assert.Throws<InvalidOperationException>(() => CalibrationProfilePolicy.PrepareForStorage(
            Profile("wrong-grating", 600, CalibrationProfileScope.Hardware),
            options));
        Assert.Throws<InvalidOperationException>(() => CalibrationProfilePolicy.PrepareForStorage(
            Profile("unscoped", 300, CalibrationProfileScope.Unspecified),
            options));
    }

    [Fact]
    public void LegacySimDefaultRemainsAvailableOnlyInSimulatorMode()
    {
        var profile = Profile("sim-default", 600, CalibrationProfileScope.Unspecified);
        var options = RealOptions();
        options.Simulator = true;

        Assert.True(CalibrationProfilePolicy.IsCompatible(profile, options));
    }

    private static UvexSafetyOptions RealOptions() => new()
    {
        Simulator = false,
        PortName = "COM5",
        ExpectedUsbVid = "1A86",
        ExpectedUsbPid = "7523",
        ExpectedGratingLinesPerMm = 300,
    };

    private static CalibrationProfile Profile(
        string id,
        int gratingLinesPerMm,
        CalibrationProfileScope scope) => new(
            id,
            id,
            gratingLinesPerMm,
            2,
            1,
            0,
            0,
            3840,
            2160,
            "Horizontal",
            1,
            [400, 0.1],
            [486.1, 656.3],
            DateTimeOffset.UtcNow,
            scope);
}
