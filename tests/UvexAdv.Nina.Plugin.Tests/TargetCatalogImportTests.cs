using System.Text.Json;
using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class TargetCatalogImportTests
{
    private static readonly DateTimeOffset CapturedUtc = new(2026, 9, 13, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void StellariumLocalizedTypeKeepsEnglishObjectTypeAndMagnitude()
    {
        using var document = JsonDocument.Parse("""
            {"found":true,"raJ2000":10,"decJ2000":20,"type":"行星状星云","object-type":"planetary nebula","vmag":8.2}
            """);
        var result = NinaPlanetariumTargetSource.ParseStellariumCatalogMetadata(document.RootElement,
            "Example", "NGC 123", new(10, 20), CapturedUtc)!;
        Assert.Equal("行星状星云", result.ObjectType);
        Assert.Equal("planetary nebula", result.ObjectSubtype);
        Assert.Equal(8.2, result.VisualMagnitude);
        Assert.Equal(TargetObservabilityClass.CompactExtended, TargetCatalogClassifier.Classify(result).PreferredClass);
        Assert.Equal(CapturedUtc, result.CapturedUtc);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("99")]
    [InlineData("\"unknown\"")]
    public void MissingOrSentinelMagnitudeRemainsUnknown(string magnitude)
    {
        using var document = JsonDocument.Parse($$"""
            {"raJ2000":10,"decJ2000":20,"type":"Star","vmag":{{magnitude}}}
            """);
        var result = NinaPlanetariumTargetSource.ParseStellariumCatalogMetadata(document.RootElement,
            "Example", "HIP 123", new(10, 20), CapturedUtc)!;
        Assert.Null(result.VisualMagnitude);
    }

    [Theory]
    [InlineData("{\"found\":false}")]
    [InlineData("{\"raJ2000\":11,\"decJ2000\":20,\"type\":\"Star\"}")]
    [InlineData("{\"raJ2000\":\"10\",\"decJ2000\":20}")]
    [InlineData("{\"raJ2000\":10,\"decJ2000\":99}")]
    [InlineData("[]")]
    public void MalformedOrChangedSelectionCannotAttachMetadata(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Null(NinaPlanetariumTargetSource.ParseStellariumCatalogMetadata(document.RootElement,
            "Example", "HIP 123", new(10, 20), CapturedUtc));
    }

    [Fact]
    public async Task ImportedMetadataIsDetachedAndMismatchedMetadataIsDiscarded()
    {
        var metadata = new TargetCatalogMetadata("Example", "HIP 123", 10, 20, "Stellarium", "Star", 2, CapturedUtc);
        var snapshot = new ObservationPlanetariumTargetSnapshot("Example", "HIP 123", new(10, 20), null, "Stellarium", CatalogMetadata: metadata);
        var service = new ObservationTargetImportService(new EmptyFramingSource(), new SnapshotSource(snapshot));
        Assert.Equal(metadata, (await service.ImportFromPlanetariumAsync()).CatalogMetadata);

        service = new ObservationTargetImportService(new EmptyFramingSource(), new SnapshotSource(snapshot with { TargetName = "Other" }));
        var result = await service.ImportFromPlanetariumAsync();
        Assert.Equal("Other", result.TargetName);
        Assert.Null(result.CatalogMetadata);
    }

    private sealed class EmptyFramingSource : IObservationFramingTargetSource
    {
        public ObservationFramingTargetSnapshot Capture() => throw new NotSupportedException();
    }

    private sealed class SnapshotSource(ObservationPlanetariumTargetSnapshot snapshot) : IObservationPlanetariumTargetSource
    {
        public Task<ObservationPlanetariumTargetSnapshot> CaptureAsync(CancellationToken cancellationToken) => Task.FromResult(snapshot);
    }
}
