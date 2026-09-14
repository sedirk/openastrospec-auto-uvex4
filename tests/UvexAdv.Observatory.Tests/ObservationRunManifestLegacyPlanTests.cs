using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Observatory.Tests;

public sealed class ObservationRunManifestLegacyPlanTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "uvex-legacy-plan-" + Guid.NewGuid().ToString("N"));
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() },
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyAbsentCatalogueFieldReadsOriginalHashAndTerminalStateWithoutRewriting(bool terminal)
    {
        var (store, json) = await CreateLegacy(terminal);
        var before = await File.ReadAllBytesAsync(store.ManifestPath);
        var read = (await new ObservationRunJournalStore(store.ManifestPath).ReadAsync())!;
        Assert.Equal(json["planSha256"]!.GetValue<string>(), read.PlanSha256);
        Assert.Null(read.Plan.CatalogMetadata);
        Assert.Equal(TargetObservabilityClass.DirectStellar, read.Plan.TargetObservability);
        Assert.Equal(terminal ? ObservationRunState.Cancelled : (ObservationRunState?)null, read.TerminalState);
        Assert.Equal(before, await File.ReadAllBytesAsync(store.ManifestPath));
        Assert.Equal(12, read.Plan.Motion.MaximumCorrectionAttempts);
    }

    [Fact]
    public async Task LegacyReadCannotImplicitlyMigrateOrReinitializeTheHistoricalFile()
    {
        var (store, _) = await CreateLegacy(false);
        var before = await File.ReadAllBytesAsync(store.ManifestPath);
        var legacy = (await store.ReadAsync())!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.PublishGateAsync(
            ObservationStage.PlaceTargetOnSlit, GateResult.Pass("TEST", "Do not rewrite history")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.InitializeAsync(legacy.Plan));
        Assert.Equal(before, await File.ReadAllBytesAsync(store.ManifestPath));
    }

    [Theory]
    [InlineData("target")]
    [InlineData("motion")]
    [InlineData("safety")]
    [InlineData("run")]
    [InlineData("metadata")]
    [InlineData("explicit-null")]
    [InlineData("unbound-extra")]
    [InlineData("omitted-safety")]
    public async Task LegacyCompatibilityStillRejectsChangedBoundValues(string mutation)
    {
        var (store, json) = await CreateLegacy(false);
        var plan = json["plan"]!.AsObject();
        switch (mutation)
        {
            case "target": plan["target"]!["rightAscensionDegrees"] = 270; break;
            case "motion": plan["motion"]!["maximumCorrectionAttempts"] = 99; break;
            case "safety": plan["requireSafetyMonitor"] = true; break;
            case "run": plan["observationRunId"] = "another-run"; break;
            case "metadata": json["lockedMetadata"]!["labels"]!["telescopeId"] = "another-mount"; break;
            case "explicit-null": plan["catalogMetadata"] = null; break;
            case "unbound-extra": plan["futureMotionAuthority"] = true; break;
            case "omitted-safety": plan.Remove("requireSafetyMonitor"); break;
        }
        await File.WriteAllTextAsync(store.ManifestPath, json.ToJsonString(Options));
        await Assert.ThrowsAsync<ObservationRunManifestCorruptException>(() => store.ReadAsync());
    }

    [Theory]
    [InlineData("future-field")]
    [InlineData("missing-safety")]
    public async Task AValidRawHashDoesNotAuthorizeUnknownOrIncompletePlanSchemas(string mutation)
    {
        var (store, json) = await CreateLegacy(false);
        var plan = json["plan"]!.AsObject();
        if (mutation == "future-field") plan["futureMotionAuthority"] = true;
        else plan.Remove("requireSafetyMonitor");
        json["planSha256"] = Hash(plan);
        await File.WriteAllTextAsync(store.ManifestPath, json.ToJsonString(Options));
        await Assert.ThrowsAsync<ObservationRunManifestCorruptException>(() => store.ReadAsync());
    }

    [Fact]
    public async Task NeitherCurrentNorLegacyReadAcceptsAnArbitraryPlanHash()
    {
        var store = await CreateCurrent(false);
        var json = JsonNode.Parse(await File.ReadAllTextAsync(store.ManifestPath))!;
        json["plan"]!.AsObject().Remove("catalogMetadata");
        json["planSha256"] = new string('A', 64);
        await File.WriteAllTextAsync(store.ManifestPath, json.ToJsonString(Options));
        await Assert.ThrowsAsync<ObservationRunManifestCorruptException>(() => store.ReadAsync());
    }

    private async Task<(ObservationRunJournalStore Store, JsonObject Json)> CreateLegacy(bool terminal)
    {
        var store = await CreateCurrent(terminal);
        var model = (await store.ReadAsync())!.Plan;
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            if (info.Type == typeof(ObservationPlan))
                info.Properties.Remove(info.Properties.Single(property => property.Name == "catalogMetadata"));
        });
        var bytes = JsonSerializer.SerializeToUtf8Bytes(model, new JsonSerializerOptions(Options) { TypeInfoResolver = resolver });
        var json = JsonNode.Parse(await File.ReadAllTextAsync(store.ManifestPath))!.AsObject();
        json["plan"] = JsonNode.Parse(bytes);
        json["planSha256"] = Convert.ToHexString(SHA256.HashData(bytes));
        // A JSON tree re-encodes '+08:00' as '\u002B08:00'. The historical
        // hash was computed with the typed DateTimeOffset converter, so this
        // fixture deliberately proves that read-back uses that original contract.
        await File.WriteAllTextAsync(store.ManifestPath, json.ToJsonString(Options));
        return (store, json);
    }

    private async Task<ObservationRunJournalStore> CreateCurrent(bool terminal)
    {
        var store = new ObservationRunJournalStore(Path.Combine(directory, "manifest.json"));
        var plan = new ObservationPlan("legacy-run", "night-setup",
            new("Vega 织女一", "HIP 91262", 279.23977705014045, 38.79037337258107),
            new(33, 120, 0), DateTimeOffset.Parse("2026-09-04T23:06:34.5951636+08:00"),
            TimeSpan.FromHours(1), new(), new(MaximumCorrectionAttempts: 12), "spectral", "guide", "photometry",
            RequireSafetyMonitor: false);
        var initial = await store.InitializeAsync(plan, new(Labels: new Dictionary<string, string> { ["telescopeId"] = "main-mount" }));
        if (terminal) await store.PublishSnapshotAsync(initial.Snapshot with { State = ObservationRunState.Cancelled });
        return store;
    }

    private static string Hash(JsonNode node) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(node, Options)));
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
}
