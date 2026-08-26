using System.Reflection;
using NINA.Profile.Interfaces;
using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class G3EffectiveSaturationTests
{
    [Fact]
    public void NativeTwelveBitPlateauIsRejectedAsSaturated()
    {
        const int width = 96;
        const int height = 96;
        var pixels = Enumerable.Repeat((ushort)4095, width * height).ToArray();
        var configuration = new G3RunConfiguration(
            1000, 50, 1, 4095, 1000, 2.4, false, 5,
            SolveExposurePreset(),
            WcsCenteringLimits(),
            60,
            120,
            3,
            WideToSlitTransferMode.Skip,
            SearchLimits());
        var off = G3FrameInputPolicy.Create(width, height, pixels, configuration);
        var on = G3FrameInputPolicy.Create(width, height, pixels, configuration);
        var seed = new SlitGeometry(
            "saturation-test",
            new PixelPoint(width / 2d, height / 2d),
            90,
            60,
            4,
            1,
            "G3M2210M-test",
            1,
            1);

        var result = SlitIlluminationPairAnalyzer.Analyze(off, on, seed);

        Assert.Equal((ushort)4095, off.SaturationLevel);
        Assert.Equal(GateDisposition.Failed, result.Gate.Disposition);
        Assert.Equal("SLIT_LED_PAIR_SATURATED", result.Gate.Code);
        Assert.Equal(1, result.SaturatedFraction, precision: 12);
    }

    [Fact]
    public void ExplicitSixteenBitConfigurationDoesNotTreat4095AsFullWell()
    {
        const int width = 96;
        const int height = 96;
        var pixels = Enumerable.Repeat((ushort)4095, width * height).ToArray();
        var configuration = new G3RunConfiguration(
            1000, 50, 1, ushort.MaxValue, 1000, 2.4, false, 5,
            SolveExposurePreset(),
            WcsCenteringLimits(),
            60,
            120,
            3,
            WideToSlitTransferMode.Skip,
            SearchLimits());
        var off = G3FrameInputPolicy.Create(width, height, pixels, configuration);
        var on = G3FrameInputPolicy.Create(width, height, pixels, configuration);
        var seed = new SlitGeometry(
            "sixteen-bit-test",
            new PixelPoint(width / 2d, height / 2d),
            90,
            60,
            4,
            1,
            "G3-16-bit-test",
            1,
            1);

        var result = SlitIlluminationPairAnalyzer.Analyze(off, on, seed);

        Assert.Equal(ushort.MaxValue, off.SaturationLevel);
        Assert.Equal(0, result.SaturatedFraction, precision: 12);
        Assert.NotEqual("SLIT_LED_PAIR_SATURATED", result.Gate.Code);
    }

    [Fact]
    public void ChangingG3SaturationAfterCaptureIsProfileDrift()
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        var accessor = CreateProxy<IPluginOptionsAccessor>((method, arguments) =>
        {
            var name = (string)arguments[0]!;
            if (method.Name.StartsWith("GetValue", StringComparison.Ordinal))
            {
                return values.TryGetValue(name, out var value) ? value : arguments[1];
            }
            if (method.Name.StartsWith("SetValue", StringComparison.Ordinal))
            {
                values[name] = arguments[1];
                return null;
            }
            throw new NotSupportedException(method.ToString());
        });
        var astrometry = CreateProxy<IAstrometrySettings>((method, _) => method.Name switch
        {
            "get_Latitude" => 33.37583333,
            "get_Longitude" => 120.41666667,
            "get_Elevation" => 0d,
            _ when method.Name.StartsWith("set_", StringComparison.Ordinal) => null,
            _ => Default(method.ReturnType),
        });
        var profile = CreateProxy<IProfile>((method, _) => method.Name == "get_AstrometrySettings"
            ? astrometry
            : Default(method.ReturnType));
        var profileService = CreateProxy<IProfileService>((method, _) => method.Name == "get_ActiveProfile"
            ? profile
            : Default(method.ReturnType));
        var settings = new UvexPluginSettings(profileService, accessor);
        var solver = new PlateSolverRunConfiguration(
            "primary", "blind", "primary-type", "blind-type",
            10, 10, 2, 500, true, 0.1, 5, 3,
            string.Empty, string.Empty, string.Empty, string.Empty,
            string.Empty, string.Empty, string.Empty, string.Empty);

        var locked = RealRunConfiguration.Capture(settings, solver);
        Assert.Equal(UvexPluginSettings.G3M2210mDefaultSaturationAdu, locked.G3.SaturationAdu);
        Assert.True(locked.MatchesCurrentProfile(settings, solver, out var unchangedHash));
        Assert.Equal(locked.ActionConfigurationSha256, unchangedHash);

        settings.G3SaturationAdu = ushort.MaxValue;

        Assert.False(locked.MatchesCurrentProfile(settings, solver, out var driftedHash));
        Assert.NotEqual(locked.ActionConfigurationSha256, driftedHash);

        settings.G3SaturationAdu = UvexPluginSettings.G3M2210mDefaultSaturationAdu;
        settings.QhyCoarseMaximumSingleCorrectionArcseconds = 600;
        settings.QhyCoarseMaximumCumulativeCorrectionArcseconds = 2400;
        settings.QhyCoarseMaximumCorrectionAttempts = 8;
        settings.QhyCoarseMaximumCenteringMinutes = 10;
        var coarseLocked = RealRunConfiguration.Capture(settings, solver);
        Assert.Equal(QhyCoarseCenteringLimits.CurrentSchemaVersion, coarseLocked.Qhy.CoarseCenteringLimits.SchemaVersion);
        Assert.Equal(600, coarseLocked.Qhy.CoarseCenteringLimits.MaximumSingleCorrectionArcseconds);

        settings.QhyCoarseMaximumSingleCorrectionArcseconds = 601;

        Assert.False(coarseLocked.MatchesCurrentProfile(settings, solver, out var coarseDriftedHash));
        Assert.NotEqual(coarseLocked.ActionConfigurationSha256, coarseDriftedHash);

        settings.QhyCoarseMaximumSingleCorrectionArcseconds = 600;
        settings.QhyG3FastPairEnabled = true;
        settings.QhyG3FastPairExposureSeconds = 1.5;
        var pairLocked = RealRunConfiguration.Capture(settings, solver);
        Assert.True(pairLocked.G3.EffectiveFastSolvePair.Enabled);
        Assert.Equal(1.5, pairLocked.G3.EffectiveFastSolvePair.QuickQhyExposureSeconds);

        settings.QhyG3FastPairMaximumMidpointSeparationSeconds++;
        Assert.False(pairLocked.MatchesCurrentProfile(settings, solver, out var pairDriftedHash));
        Assert.NotEqual(pairLocked.ActionConfigurationSha256, pairDriftedHash);

        var patternLocked = RealRunConfiguration.Capture(
            settings,
            solver,
            NinaImageFilePatternPolicy.RecommendedPattern);
        Assert.True(patternLocked.MatchesCurrentProfile(
            settings,
            solver,
            NinaImageFilePatternPolicy.RecommendedPattern,
            out var samePatternHash));
        Assert.Equal(patternLocked.ActionConfigurationSha256, samePatternHash);
        Assert.False(patternLocked.MatchesCurrentProfile(
            settings,
            solver,
            "$$TARGETNAME$$\\changed\\$$DATETIME$$",
            out var changedPatternHash));
        Assert.NotEqual(patternLocked.ActionConfigurationSha256, changedPatternHash);
    }

    [Fact]
    public void HardwareG3DefaultsFreezeTenSecondsFullGainAndSeparateRuntimeNames()
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        var accessor = CreateProxy<IPluginOptionsAccessor>((method, arguments) =>
        {
            var name = (string)arguments[0]!;
            if (method.Name.StartsWith("GetValue", StringComparison.Ordinal))
            {
                return values.TryGetValue(name, out var value) ? value : arguments[1];
            }
            if (method.Name.StartsWith("SetValue", StringComparison.Ordinal))
            {
                values[name] = arguments[1];
                return null;
            }
            throw new NotSupportedException(method.ToString());
        });
        var astrometry = CreateProxy<IAstrometrySettings>((method, _) => method.Name switch
        {
            "get_Latitude" => 33.37583333,
            "get_Longitude" => 120.41666667,
            "get_Elevation" => 0d,
            _ when method.Name.StartsWith("set_", StringComparison.Ordinal) => null,
            _ => Default(method.ReturnType),
        });
        var profile = CreateProxy<IProfile>((method, _) => method.Name == "get_AstrometrySettings"
            ? astrometry
            : Default(method.ReturnType));
        var profileService = CreateProxy<IProfileService>((method, _) => method.Name == "get_ActiveProfile"
            ? profile
            : Default(method.ReturnType));
        var settings = new UvexPluginSettings(profileService, accessor);

        Assert.Equal(60, settings.ObservationDurationMinutes);
        Assert.Equal(10_000, settings.G3ExposureMilliseconds);
        Assert.Equal(100, settings.G3GainPercent);
        Assert.Equal(60, settings.G3WcsFreshSolveAuthorizationResidualArcseconds);
        Assert.Equal("G3M2210M", settings.Phd2RuntimeCameraName);
        Assert.Equal("On-Step (ASCOM)", settings.Phd2RuntimeMountName);
        Assert.Equal(WideToSlitTransferMode.Skip, settings.WideToSlitTransferMode);
        Assert.False(settings.QhyG3FastPairEnabled);
        Assert.Equal(QhyG3FastPairPolicy.CurrentSchemaVersion, settings.QhyG3FastPairSchemaVersion);
        Assert.Equal(2, settings.QhyG3FastPairExposureSeconds);
        Assert.Equal(QhyCoarseCenteringLimits.CurrentSchemaVersion, settings.QhyCoarseCenteringSchemaVersion);
        Assert.Equal(0, settings.QhyCoarseMaximumSingleCorrectionArcseconds);
        Assert.Equal(0, settings.QhyCoarseMaximumCumulativeCorrectionArcseconds);
        Assert.Equal(0, settings.QhyCoarseMaximumCorrectionAttempts);
        Assert.Equal(0, settings.QhyCoarseMaximumCenteringMinutes);
        Assert.Equal(0, settings.QhyMinimumDetectedStars);
        Assert.Equal(0, settings.QhyMinimumTransparency);
        Assert.Equal(0.002, settings.QhyMaximumSaturatedFraction);

        var solver = new PlateSolverRunConfiguration(
            "primary", "blind", "primary-type", "blind-type",
            10, 10, 2, 500, true, 0.1, 5, 3,
            string.Empty, string.Empty, string.Empty, string.Empty,
            string.Empty, string.Empty, string.Empty, string.Empty);
        var locked = RealRunConfiguration.Capture(settings, solver);

        Assert.Equal(0, locked.Qhy.QualityThresholds.MinimumDetectedStars);
        Assert.Equal(0, locked.Qhy.QualityThresholds.MinimumTransparency);
        Assert.Equal(0.002, locked.Qhy.QualityThresholds.MaximumSaturatedFraction);
    }

    private static G3LocalSearchLimits SearchLimits() => new(
        G3LocalSearchPattern.SquareSpiral,
        10,
        30,
        120,
        4,
        TimeSpan.FromMinutes(5));

    private static G3PlateSolveExposurePreset SolveExposurePreset() => new(
        G3PlateSolveExposurePreset.CurrentSchemaVersion,
        "test-ladder",
        [2_000, 5_000, 10_000]);

    private static G3WcsCenteringLimits WcsCenteringLimits() => new(
        G3WcsCenteringLimits.CurrentSchemaVersion,
        30,
        120,
        240,
        10,
        TimeSpan.FromMinutes(5),
        0);

    private static T CreateProxy<T>(Func<MethodInfo, object?[], object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, InterfaceDispatchProxy>();
        ((InterfaceDispatchProxy)(object)proxy).Handler = handler;
        return proxy;
    }

    private static object? Default(Type type) => type == typeof(void)
        ? null
        : type.IsValueType
            ? Activator.CreateInstance(type)
            : null;

    private class InterfaceDispatchProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[], object?> Handler { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            Handler(targetMethod!, args ?? []);
    }
}
