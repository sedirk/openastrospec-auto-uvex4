using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Observatory.Tests;

public sealed class TargetCatalogClassifierTests
{
    private static readonly EquatorialTarget Target = new("Example", "NGC 123", 10, 20);
    private static TargetCatalogMetadata Metadata(string? type, string? subtype = null, double? magnitude = 4) =>
        new(Target.Name, Target.CatalogId, 10, 20, "Stellarium", type, magnitude,
            new DateTimeOffset(2026, 9, 13, 20, 0, 0, TimeSpan.Zero), subtype);

    [Theory]
    [InlineData("Star", null, TargetObservabilityClass.DirectStellar)]
    [InlineData("Star", "double-star", TargetObservabilityClass.DirectStellar)]
    [InlineData("Nebula", "planetary nebula", TargetObservabilityClass.CompactExtended)]
    [InlineData("行星状星云", "planetary nebula", TargetObservabilityClass.CompactExtended)]
    [InlineData("Nebula", "quasar", TargetObservabilityClass.FaintPointSource)]
    [InlineData("Nebula", "emission nebula", TargetObservabilityClass.ExtendedNebula)]
    [InlineData("Nebula", "open star cluster", TargetObservabilityClass.ExtendedNebula)]
    [InlineData("Nebula", "galaxy", TargetObservabilityClass.ExtendedNebula)]
    [InlineData("Nebula", "emission-line star", TargetObservabilityClass.DirectStellar)]
    [InlineData("PN", null, TargetObservabilityClass.CompactExtended)]
    public void UsesExplicitCatalogueTypeNotObjectName(string type, string? subtype, TargetObservabilityClass expected)
    {
        var classification = TargetCatalogClassifier.Classify(Metadata(type, subtype));
        Assert.Equal(expected, classification.PreferredClass);
        Assert.True(classification.HasKnownObjectType);
        Assert.False(classification.IsUnsupportedObjectType);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(5)]
    [InlineData(18)]
    public void MagnitudeDoesNotInventGuideCameraVisibility(double magnitude)
    {
        var result = TargetCatalogClassifier.Classify(Metadata("Star", magnitude: magnitude));
        Assert.Equal(TargetObservabilityClass.DirectStellar, result.PreferredClass);
        Assert.Contains("不是导星相机可见性门限", result.Reason);
    }

    [Fact]
    public void MissingOrUnknownTypeNeverInfersFromNameOrMagnitude()
    {
        var result = TargetCatalogClassifier.Classify(Metadata(null, magnitude: 19) with { TargetName = "Orion Nebula" });
        Assert.False(result.HasKnownObjectType);
        Assert.Equal(TargetObservabilityClass.DirectStellar, result.PreferredClass);
        Assert.Contains("没有推断目标不可见", result.Reason);
        Assert.False(TargetCatalogClassifier.Classify(null).HasKnownObjectType);
    }

    [Theory]
    [InlineData("Planet", "star")]
    [InlineData("Planet", "moon")]
    [InlineData("Comet", null)]
    [InlineData("Satellite", null)]
    [InlineData("MinorPlanet", null)]
    public void SolarOrMovingTargetCannotBecomeSiderealStar(string type, string? subtype)
    {
        Assert.True(TargetCatalogClassifier.Classify(Metadata(type, subtype)).IsUnsupportedObjectType);
    }

    [Fact]
    public void IdentityBindingRejectsTargetOrCoordinateEditsAndMalformedStoredMetadata()
    {
        var metadata = Metadata("Star");
        Assert.True(TargetCatalogClassifier.IsBoundTo(metadata, Target));
        Assert.False(TargetCatalogClassifier.IsBoundTo(metadata, Target with { Name = "Other" }));
        Assert.False(TargetCatalogClassifier.IsBoundTo(metadata, Target with { CatalogId = "HIP 1" }));
        Assert.False(TargetCatalogClassifier.IsBoundTo(metadata, Target with { RightAscensionDegrees = 10.001 }));
        Assert.False(TargetCatalogClassifier.IsBoundTo(metadata with { DeclinationDegrees = double.NaN }, Target));
        Assert.Null(TargetCatalogMetadataSerialization.Read("{"));
        Assert.Null(TargetCatalogMetadataSerialization.Read("{}"));
        Assert.Equal(metadata, TargetCatalogMetadataSerialization.Read(TargetCatalogMetadataSerialization.Write(metadata)));
        Assert.Equal(string.Empty, TargetCatalogMetadataSerialization.Write(null));
    }
}
