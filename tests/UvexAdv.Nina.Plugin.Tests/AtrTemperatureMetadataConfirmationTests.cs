using NINA.Image.FileFormat.FITS;
using NINA.Image.ImageData;
using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class AtrTemperatureMetadataConfirmationTests
{
    private const string Id = "ToupTek_exact-camera";
    private static AtrCoolingTelemetry Good(double temperature = -9.9) => new(true, Id, true, true, temperature, -10, 66);
    private static Task NoWait(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
    private static Task<AtrTemperatureConfirmation> Confirm(params AtrCoolingTelemetry[] values)
    {
        var queue = new Queue<AtrCoolingTelemetry>(values);
        return AtrTemperatureMetadataConfirmation.ConfirmAsync(AtrTemperatureMetadataConfirmation.DriverType,
            0, -10, -10, Id, _ => Task.FromResult(queue.Dequeue()), NoWait, CancellationToken.None);
    }

    [Fact]
    public async Task UsesLastFreshMeasuredValueNotSetpointOrCachedNativeZero()
    {
        var r = await Confirm(Good(-9.8), Good(-9.9), Good(-9.7));
        Assert.True(r.Applied);
        Assert.Equal(-9.7, r.ConfirmedTemperatureC);
        Assert.Equal(0, r.OriginalTemperatureC);
        Assert.NotNull(r.ConfirmedUtc);
        Assert.Equal(3, r.Samples.Count);
    }

    [Fact]
    public async Task IsolatedZeroNeedsThreeNewConsecutiveGoodSamples()
    {
        var r = await Confirm(Good(), Good(0), Good(-9.8), Good(-9.9), Good(-10));
        Assert.True(r.Applied);
        Assert.Equal(5, r.Samples.Count);
        Assert.Equal(0, r.Samples[1].TemperatureC);
    }

    [Fact]
    public async Task PersistentOrAlternatingZerosCannotBeVotedAway()
    {
        foreach (var values in new[] { Enumerable.Repeat(Good(0), 6).ToArray(),
            new[] {Good(), Good(0), Good(), Good(0), Good(), Good()} })
        {
            var r = await Confirm(values);
            Assert.False(r.Applied);
            Assert.Null(r.ConfirmedTemperatureC);
            Assert.Equal(6, r.Samples.Count);
        }
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("disconnected")]
    [InlineData("cooler-off")]
    [InlineData("setpoint")]
    [InlineData("warming")]
    [InlineData("power")]
    public async Task RealFaultsAreNotRetriedOrRewritten(string fault)
    {
        var bad = fault switch {
            "identity" => Good() with { DeviceId = "other" },
            "disconnected" => Good() with { Connected = false },
            "cooler-off" => Good() with { CoolerOn = false },
            "setpoint" => Good() with { TemperatureSetPointC = 0 },
            "warming" => Good(-5),
            _ => Good() with { CoolerPowerPercent = double.NaN }
        };
        var r = await Confirm(bad, Good(), Good(), Good());
        Assert.False(r.Applied);
        Assert.Single(r.Samples);
        var metadata = new ImageMetaData();
        metadata.Camera.Temperature = 0;
        AtrTemperatureMetadataHeaders.ApplyBeforeFirstSave(metadata, r);
        Assert.Equal(0, metadata.Camera.Temperature);
    }

    [Theory]
    [InlineData("other-driver", 0, -10, -10)]
    [InlineData(AtrTemperatureMetadataConfirmation.DriverType, -5, -10, -10)]
    [InlineData(AtrTemperatureMetadataConfirmation.DriverType, -9.9, -10, -10)]
    [InlineData(AtrTemperatureMetadataConfirmation.DriverType, 0, 0, 0)]
    [InlineData(AtrTemperatureMetadataConfirmation.DriverType, 0, 0, -10)]
    public async Task NonMatchingCasesNeverReadOwner(string driver, double native, double setpoint, double target)
    {
        var calls = 0;
        var r = await AtrTemperatureMetadataConfirmation.ConfirmAsync(driver, native, setpoint, target, Id,
            _ => {calls++; return Task.FromResult(Good());}, NoWait, CancellationToken.None);
        Assert.False(r.Applied);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ReadExceptionPreservesNativeMetadataAndCancellationPropagates()
    {
        var r = await AtrTemperatureMetadataConfirmation.ConfirmAsync(AtrTemperatureMetadataConfirmation.DriverType,
            0, -10, -10, Id, _ => throw new IOException("driver read failed"), NoWait, CancellationToken.None);
        Assert.False(r.Applied);
        Assert.Equal("OWNER_TEMPERATURE_READBACK_FAILED", r.Code);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AtrTemperatureMetadataConfirmation.ConfirmAsync(
            AtrTemperatureMetadataConfirmation.DriverType, 0, -10, -10, Id,
            _ => Task.FromResult(Good()), NoWait, cts.Token));
    }

    [Theory]
    [InlineData("PROBE", "SNAPSHOT")]
    [InlineData("SCIENCE", "LIGHT")]
    public async Task NativeFitsRoundTripRetainsZeroSourceSamplesIdentityAndWarnings(string role, string imageType)
    {
        var metadata = new ImageMetaData();
        var expected = new FitsProvenanceExpectation("WR152", "test-run", role, "capture-205", "test-night", imageType, "WR152", HeaderSchemaVersion: 2);
        metadata.Target.Name = "WR152";
        metadata.Image.ImageType = imageType;
        metadata.Camera.Id = Id;
        metadata.Camera.Temperature = 0;
        metadata.Camera.SetPoint = -10;
        metadata.GenericHeaders.Add(new StringMetaDataHeader("SLITWARN", "True", "retained warning"));
        foreach (var h in AtrFitsProvenance.CreateIdentityHeaders(expected))
            metadata.GenericHeaders.Add(new StringMetaDataHeader(h.Key, h.Value, "test identity"));
        var r = await Confirm(Good(-9.8), Good(-9.9), Good(-10));
        AtrTemperatureMetadataHeaders.ApplyBeforeFirstSave(metadata, r);
        var path = Path.Combine(Path.GetTempPath(), $"uvex-temperature-test-{Guid.NewGuid():N}.fits");
        try
        {
            var header = new FITSHeader(2, 2);
            header.Add("SIMPLE", true, "test only");
            header.PopulateFromMetaData(metadata);
            using (var stream = File.Create(path)) header.Write(stream);
            var original = File.ReadAllBytes(path);
            var verified = AtrFitsProvenance.Verify(path, expected);
            Assert.True(verified.IsValid, string.Join(";", verified.Issues));
            Assert.Equal("0", verified.Headers["UVEXTN"]);
            Assert.Equal("-9.8", verified.Headers["UVEXT1"]);
            Assert.Equal("True", verified.Headers["SLITWARN"]);
            Assert.Equal(r.Code, verified.Headers["UVEXTSRC"]);
            Assert.Equal(GateDisposition.Passed, AtrCoolingReadinessPolicy.EvaluateSavedFrame(verified.Headers, -10,
                AtrCoolingReadinessPolicy.Evaluate(Good(-10), Id, -10)).Disposition);
            Assert.Equal(original, File.ReadAllBytes(path));
            // A healthy later camera reading still cannot accept an old zero-temperature FITS.
            var old = new Dictionary<string, string>(verified.Headers) { ["CCD-TEMP"] = "0" };
            Assert.NotEqual(GateDisposition.Passed, AtrCoolingReadinessPolicy.EvaluateSavedFrame(old, -10,
                AtrCoolingReadinessPolicy.Evaluate(Good(-10), Id, -10)).Disposition);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SharedRunnerConfirmsBeforeFirstSaveAndStillChecksSavedTemperatureBeforeAcceptance()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "RealObservationStageRunner.cs"));
        var capture = source.IndexOf("var image = await exposure.ToImageData", StringComparison.Ordinal);
        var confirm = source.IndexOf("await ConfirmAtrTemperatureMetadataBeforeFirstSaveAsync", capture, StringComparison.Ordinal);
        Assert.True(confirm > capture && confirm < source.IndexOf("return new AtrCapture(", capture, StringComparison.Ordinal));
        Assert.Contains("await ReadAtrPostSaveTemperatureAsync(capture, cancellationToken)", source);
        Assert.Contains("provenance.Headers, configuration.Atr.TargetTemperatureC", source);
    }
}
