using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

/// <summary>
/// Exercise actual image failure producers before the real native night/target
/// containers. Device owners are finite test doubles; no camera or mount is opened.
/// </summary>
public sealed class G3ShortFailureNativeBoundaryTests
{
    private static readonly PixelPoint Prediction = new(510, 400);
    private static readonly string FirstHash = new('a', 64);
    private static readonly string SecondHash = new('b', 64);

    [Theory]
    [InlineData("unmeasured")]
    [InlineData("persistent-blend")]
    [InlineData("positions-disagree")]
    public async Task ExhaustedShortMeasurementsActuallyExecuteTheNextNativeTarget(string failure)
    {
        var (prior, current) = Measurements(failure);
        var gate = PostWcsFailure(prior, current);
        Assert.Contains(failure switch
        {
            "unmeasured" => "G3_SEP_UNMEASURED",
            "persistent-blend" => "G3_SEP_PARENT_BLEND_UNRESOLVED",
            _ => "G3_SHORT_REPEAT_DISAGREES",
        }, gate.Message);
        await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync("skip", gate.Code,
            gate, ObservationStage.AcquireG3SlitField);
    }

    [Theory]
    [InlineData("skip")]
    [InlineData("cleanup-failure")]
    [InlineData("end-policy")]
    [InlineData("late-cancel")]
    [InlineData("late-unsafe")]
    [InlineData("late-end")]
    [InlineData("late-deadline")]
    [InlineData("late-dawn")]
    public async Task CatalogueShortFrameExhaustionUsesTheSameCheckedNativeBoundary(string boundary)
    {
        // One valid anchor then insufficient supported signal through the final
        // allowed image is still a failure, even with bounded exposure adaptation.
        var (prior, current) = Measurements("unmeasured");
        var confirmation = G3ShortPositionMeasurementPolicy.EvaluateConfirmation(prior, current,
            FirstHash, SecondHash, 6, 100, 6);
        Assert.False(confirmation.Accepted);
        Assert.False(confirmation.RetryAllowed);
        Assert.Equal("G3_SEP_UNMEASURED", confirmation.Gate.Code);
        var gate = RealObservationStageRunner.CreateCatalogShortPositionFailure(
            $"Short position confirmation exhausted: {confirmation.Gate.Code}: {confirmation.Gate.Message}",
            confirmation.Gate.Metrics);
        Assert.Equal("G3_CATALOG_SHORT_POSITION_UNCONFIRMED", gate.Code);
        Assert.Equal(6, gate.Metrics!["shortPositionFrames"]);
        await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync(boundary, gate.Code,
            gate, ObservationStage.AcquireG3SlitField);
    }

    [Theory]
    [InlineData("cleanup-failure")]
    [InlineData("end-policy")]
    [InlineData("late-cancel")]
    [InlineData("late-unsafe")]
    [InlineData("late-end")]
    [InlineData("late-deadline")]
    [InlineData("late-dawn")]
    public async Task AnOpticalFailureCannotOverrideTheActualStopOrNightBoundary(string boundary)
    {
        var (prior, current) = Measurements("positions-disagree");
        var gate = PostWcsFailure(prior, current);
        await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync(boundary, gate.Code,
            gate, ObservationStage.AcquireG3SlitField);
    }

    [Theory]
    [InlineData("G3_SHORT_POINTING_CHANGED")]
    [InlineData("G3_SHORT_FRAME_REUSED")]
    [InlineData("G3_SEP_INPUT_CHANGED")]
    [InlineData("PHD2_ADAPTIVE_CAPTURE_OWNER_CHANGED")]
    public async Task ARealInputOrOwnerFailureIsNotAnOpticalSkip(string code)
    {
        // These are independent producer guards, not an inner optical diagnostic
        // wrapped in SHORT_UNCONFIRMED. Never infer safety from exception wording.
        var gate = GateResult.Unknown(code, "Independent acquisition authority is invalid.");
        Assert.False(NativeSequencePlan.IsSkippableQualityFailure(gate));
        await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync("unknown-gate", code,
            gate, ObservationStage.AcquireG3SlitField);
    }

    [Fact]
    public async Task RecordedHd224559DisagreeingPairStillFailsHonestlyButRunsTheNextTarget()
    {
        var root = Environment.GetEnvironmentVariable("UVEX_REPLAY_OBSERVATIONS");
        if (string.IsNullOrWhiteSpace(root)) return; // Opt-in immutable local evidence.
        var directory = Path.Combine(root, "UVEX-20261021T140550Z-203e6c44012c406", "evidence");
        var paths = Directory.GetFiles(directory, "*-g3-post-wcs-short-target-confirmation.json")
            .Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(6, paths.Length);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new RecordedDiagnosticNumberConverter());
        using var priorDocument = JsonDocument.Parse(File.ReadAllText(paths[^2]));
        using var currentDocument = JsonDocument.Parse(File.ReadAllText(paths[^1]));
        var priorPayload = priorDocument.RootElement.GetProperty("payload");
        var currentPayload = currentDocument.RootElement.GetProperty("payload");
        var prior = priorPayload.GetProperty("measurement").Deserialize<G3ShortPositionMeasurement>(options)!;
        var current = currentPayload.GetProperty("measurement").Deserialize<G3ShortPositionMeasurement>(options)!;
        // Replay the OLD measured association, not a claim that today's improved
        // detector must select the same source or that unseen future frames passed.
        var confirmation = G3ShortPositionMeasurementPolicy.EvaluateConfirmation(prior, current,
            priorPayload.GetProperty("shortFrameSha256").GetString(),
            currentPayload.GetProperty("shortFrameSha256").GetString()!, 6, 100, 6);
        Assert.False(confirmation.Accepted);
        Assert.False(confirmation.RetryAllowed);
        Assert.Equal("G3_SHORT_REPEAT_DISAGREES", confirmation.Gate.Code);
        Assert.Equal(19.537529581497203, confirmation.PositionSpreadPixels, 9);
        var longTarget = currentPayload.GetProperty("longTarget").Deserialize<TargetIdentification>(options)!;
        var gate = G3PostWcsShortHandoffPolicy.Evaluate(longTarget, longTarget.PredictedPoint,
            5, Prediction, 1920, 1080, 20, prior, current, confirmation, 6, false, 44, 100).Gate;
        Assert.Equal("G3_POST_WCS_SHORT_UNCONFIRMED", gate.Code);
        foreach (var payload in new[] { priorPayload, currentPayload })
        {
            using var input = File.OpenRead(payload.GetProperty("shortFramePath").GetString()!);
            Assert.Equal(payload.GetProperty("shortFrameSha256").GetString()!.ToUpperInvariant(),
                Convert.ToHexString(SHA256.HashData(input)));
        }
        await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync("skip", gate.Code,
            gate, ObservationStage.AcquireG3SlitField);
    }

    private static GateResult PostWcsFailure(G3ShortPositionMeasurement prior, G3ShortPositionMeasurement current)
    {
        var confirmation = G3ShortPositionMeasurementPolicy.EvaluateConfirmation(prior, current,
            FirstHash, SecondHash, 6, 100, 6);
        Assert.False(confirmation.Accepted);
        Assert.False(confirmation.RetryAllowed);
        var longTarget = Measure(Image(511, 401)).Identification;
        var decision = G3PostWcsShortHandoffPolicy.Evaluate(longTarget, Prediction, 5,
            Prediction, 1024, 768, 20, prior, current, confirmation, 6, false, 44, 100);
        Assert.False(decision.Accepted);
        Assert.False(decision.RecheckPair);
        Assert.Equal("G3_POST_WCS_SHORT_UNCONFIRMED", decision.Gate.Code);
        Assert.Equal(6, decision.Gate.Metrics!["shortPositionFrames"]);
        return decision.Gate;
    }

    private static (G3ShortPositionMeasurement Prior, G3ShortPositionMeasurement Current) Measurements(string failure) => failure switch
    {
        "unmeasured" => (Measure(Image(511, 401)), Measure(Image(511, 401) with { Components = [], ParentComponents = [] })),
        "persistent-blend" => (Measure(Image(511, 401, split: true)), Measure(Image(512, 402, split: true))),
        _ => (Measure(Image(511, 401)), Measure(Image(530, 401))),
    };

    private static G3ShortPositionMeasurement Measure(SepImageMeasurements image) =>
        G3SepShortPositionPolicy.Measure(image, Prediction, 100);

    private static SepImageMeasurements Image(double x, double y, bool split = false)
    {
        var parent = new SepPositionComponent(x, y, 4, 3, 10000, 3000, 30, 0, 20, 40,
            [(int)x - 10, (int)y - 10, 21, 21], 0, 1);
        return new(1, SepStarDetectionClient.Algorithm, 1024, 768, [], false, false)
        {
            ParentComponents = [parent],
            Components = split ? [parent with { X = x - 2 }, parent with { X = x + 2 }] : [parent],
        };
    }

    // The evidence writer records non-finite diagnostics (e.g. an absent
    // uniqueness competitor) as null. Restore only an unknown sentinel, not a
    // fabricated finite confidence or a production ledger deserialization rule.
    private sealed class RecordedDiagnosticNumberConverter : JsonConverter<double>
    {
        public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.Null ? double.NaN : reader.GetDouble();
        public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options) =>
            throw new NotSupportedException("Recorded evidence is read-only.");
    }
}
