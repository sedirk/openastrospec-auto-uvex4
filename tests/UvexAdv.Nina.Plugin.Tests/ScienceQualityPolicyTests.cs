using System.Security.Cryptography;
using System.Globalization;
using System.Text.Json;
using UvexAdv.Observatory;
using UvexAdv.Spectroscopy;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class ScienceQualityPolicyTests
{
    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public void MeasuredProbeConsentIsIndependentOfLegacySupervision(bool warning, bool weak)
    {
        var policy = new ScienceQualityPolicy(warning, weak);
        var residuals = new[] { 4.503999163721631, 3.327991384830287, 5.140441884168473 };
        Phd2SlitApertureResidual?[] apertures = [
            new(residuals[0], -4.503936030352457, -.02384745894459997, 205, .5),
            new(residuals[1], -1.6272487525660548, -2.903030856669772, 205, .5),
            new(residuals[2], -3.9072635658520474, -3.340274568277085, 205, .5)];
        Assert.Equal(warning || weak, policy.AllowsMeasuredContinuation);
        Assert.Equal(warning, policy.EvaluateSlitWarning(true, true, residuals, apertures, 2, 2, 20).CanProbe);
        Assert.Equal(warning, policy.AllowsPreExposureWarning(true, true, residuals, 20));
        Assert.False(policy.EvaluateSlitWarning(false, true, residuals, apertures, 2, 2, 20).CanProbe);
        Assert.False(policy.EvaluateSlitWarning(true, false, residuals, apertures, 2, 2, 20).CanProbe);
        Assert.False(policy.AllowsPreExposureWarning(false, true, residuals, 20));
        Assert.False(policy.AllowsPreExposureWarning(true, false, residuals, 20));
        Assert.False(policy.AllowsPreExposureWarning(true, true, [1], 20));
        Assert.False(policy.AllowsPreExposureWarning(true, true, [1, 1, double.NaN], 20));
        Assert.False(policy.AllowsPreExposureWarning(true, true, [1, 1, 21], 20));
    }

    [Fact]
    public void TypedQualityScopeDoesNotReclassifyUnknownOrUnconfirmedStop()
    {
        var failure = new TargetQualityEvidenceException("PHD2_FRESH_GUIDE_WINDOW_DEADLINE", "fixture");
        Assert.True(NativeSequencePlan.IsSkippableQualityFailure(failure.AfterStop(true).Gate));
        Assert.Equal(GateFailureScope.TargetQuality, failure.Gate.FailureScope);
        Assert.False(NativeSequencePlan.IsSkippableQualityFailure(failure.AfterStop(false).Gate));
        Assert.False(NativeSequencePlan.IsSkippableQualityFailure(GateResult.Unknown(failure.Gate.Code, "untyped")));
        Assert.False(NativeSequencePlan.IsSkippableQualityFailure(GateResult.Pass("fixture", "passed") with
            { FailureScope = GateFailureScope.TargetQuality }));
    }

    // Opt-in local regression reads immutable originals only; never opens a
    // camera, re-runs PHD2, modifies the observation, or claims new sky success.
    [Fact]
    public async Task RecordedProductiveRunsRetainTheirMeasuredProbeRoute()
    {
        var root = Environment.GetEnvironmentVariable("UVEX_REPLAY_OBSERVATIONS");
        if (string.IsNullOrWhiteSpace(root)) return;
        string[] ids = [
            "UVEX-20261002T135837Z-05fe2b8e113c478", "UVEX-20261002T143511Z-b7aff222ece0482",
            "UVEX-20261002T161112Z-bea193475e354f5", "UVEX-20261004T160744Z-72f8a935699743d",
            "UVEX-20261005T111635Z-b188cc61bf2c45e", "UVEX-20261005T192232Z-b9d5f3375a81439",
            "UVEX-20261007T132717Z-c7c518f94151439", "UVEX-20261007T161631Z-25db169c1e3145b",
            "UVEX-20261008T170531Z-f242c61e7e2a486",
            "UVEX-20261014T112329Z-1862faa86ec84de", "UVEX-20261014T114529Z-20a16ea8c244410",
            "UVEX-20261014T121814Z-bfef8e1f7392465", "UVEX-20261014T125018Z-07288c0a1416434",
            "UVEX-20261011T190811Z-8c7a4b56c1e740b", "UVEX-20261013T162452Z-b53628410c2e452"];
        var reports = new List<object>();
        foreach (var id in ids)
        {
            var directory = Path.Combine(root, id);
            var manifestPath = Path.Combine(directory, "manifest.json");
            var manifestBytes = File.ReadAllBytes(manifestPath);
            using var manifest = JsonDocument.Parse(manifestBytes);
            var accepted = manifest.RootElement.GetProperty("counters").GetProperty("atrAcceptedFrames").GetInt32();
            Assert.True(accepted > 0);
            var evidencePath = Assert.Single(Directory.GetFiles(Path.Combine(directory, "evidence"),
                "*phd2-supervised-slit-warning-probe-authorized.json"));
            var bytes = File.ReadAllBytes(evidencePath);
            using var evidence = JsonDocument.Parse(bytes);
            var payload = evidence.RootElement.GetProperty("payload");
            var residuals = payload.GetProperty("completionResiduals").EnumerateArray().Select(x => x.GetDouble()).ToArray();
            var apertures = payload.TryGetProperty("slitApertureResiduals", out var a)
                ? a.EnumerateArray().Select(x => (Phd2SlitApertureResidual?)new(
                    x.GetProperty("midpointPixels").GetDouble(), x.GetProperty("alongSlitPixels").GetDouble(),
                    x.GetProperty("crossSlitPixels").GetDouble(), x.GetProperty("halfLengthPixels").GetDouble(),
                    x.GetProperty("uncertaintyPixels").GetDouble())).ToArray() : [];
            var tolerance = payload.GetProperty("tolerance").GetDouble();
            Assert.True(payload.GetProperty("operatorConsent").GetBoolean());
            Assert.False(payload.GetProperty("exactSlitPlacementProven").GetBoolean());
            var source = evidence.RootElement.GetProperty("source");
            var rawPath = source.GetProperty("absolutePath").GetString()!;
            var rawHash = Hash(rawPath);
            Assert.Equal(source.GetProperty("sha256").GetString()!.ToUpperInvariant(), rawHash);
            foreach (var weak in new[] { false, true })
            {
                Assert.True(new ScienceQualityPolicy(true, weak).EvaluateSlitWarning(
                    true, true, residuals, apertures, tolerance, 2, 20).CanProbe, id);
                Assert.False(new ScienceQualityPolicy(false, weak).EvaluateSlitWarning(
                    true, true, residuals, apertures, tolerance, 2, 20).CanProbe, id);
            }
            // Drive the extracted controllers with recorded numbers through
            // strict non-device ports. Identity/safety are controlled fixtures,
            // not renewed real-world authority from an old observation.
            var convergence = new Phd2ConvergenceFixture(warning: true);
            var request = convergence.Request(residuals);
            request = request with { Preset = request.Preset with
                { TargetOnSlitTolerancePixels = tolerance, MaximumResidualGrowthPixels = 2, MaximumAcquisitionResidualPixels = 20 } };
            if (apertures.Length == residuals.Length)
            {
                var window = request.InitialWindow.Select((frame, i) => frame with
                {
                    Measurement = frame.Measurement with { TargetCentroid = new(
                        500 + apertures[i]!.AlongSlitPixels, 400 + apertures[i]!.CrossSlitPixels) },
                    RuntimeSlitLocal = frame.RuntimeSlitLocal with
                    { LengthPixels = apertures[i]!.HalfLengthPixels * 2, UncertaintyPixels = apertures[i]!.UncertaintyPixels },
                }).ToArray();
                request = request with { InitialWindow = window, Session = request.Session with { LastMeasurement = window[^1] } };
            }
            var placement = await convergence.Workflow.ConvergeAsync(convergence.Context, request, default);
            Assert.Equal("PHD2_SLIT_PRECISION_WARNING_PROBE_AUTHORIZED", placement.Gate.Code);
            Assert.DoesNotContain("SetExactLockPositionAsync", convergence.Trace.Calls);
            var recorded = manifest.RootElement.GetProperty("evidence").EnumerateArray()
                .Where(e => e.GetProperty("kind").GetString() is "atr-probe-fits" or "atr-science-fits")
                .DistinctBy(e => e.GetProperty("absolutePath").GetString()).ToArray();
            var probes = recorded.Where(e => e.GetProperty("kind").GetString() == "atr-probe-fits").ToArray();
            var tiers = probes.Select(e => Number(e.GetProperty("metadata"), "exposureSeconds")).Distinct().Order().ToArray();
            var rawHashes = recorded.Select(e => (Path: e.GetProperty("absolutePath").GetString()!,
                Sha: e.GetProperty("sha256").GetString()!.ToUpperInvariant())).ToArray();
            foreach (var raw in rawHashes) Assert.Equal(raw.Sha, Hash(raw.Path));
            SpectralProbeMetrics RecordedMetrics(JsonElement frame, SpectralProbeMetrics expected)
            {
                var m = frame.GetProperty("metadata");
                Assert.Equal(expected.ExposureSeconds, Number(m, "exposureSeconds"));
                return new SpectralProbeMetrics(Number(m, "exposureSeconds"), Number(m, "biasLevelAdu"),
                    Number(m, "highPercentileAdu"), Number(m, "fullScaleAdu"), Number(m, "saturatedFraction"),
                    Number(m, "continuumSnrPerResolutionElement"), Number(m, "lineSnrPerResolutionElement"),
                    Number(m, "targetToSkyContrast"), bool.Parse(m.GetProperty("guidingStable").GetString()!),
                    frame.GetProperty("absolutePath").GetString()!, Number(m, "traceSaturatedFraction"),
                    Number(m, "clippedDispersionColumnFraction"), (int)Number(m, "longestClippedDispersionColumnRun"));
            }
            var captures = new Queue<JsonElement>(recorded);
            var atr = new AtrWorkflowFixture(accepted, tiers, accepted + 4, Number(probes[0].GetProperty("metadata"), "exposureSeconds"));
            atr.Port.NextMetrics = expected => RecordedMetrics(captures.Dequeue(), expected);
            var selection = await atr.Science.SelectExposureAsync(atr.Context, default);
            Assert.True(selection.CanAdvance, $"{id}: {selection.Gate.Message}");
            var earlyHighSnrSelection = atr.Probe.AttemptedFrames < probes.Length;
            var scienceReplay = atr;
            if (earlyHighSnrSelection)
            {
                // No historical future frames exist at the newly selected tier.
                // Do not relabel 600s data as a counterfactual shorter capture.
                // Separately verify the real historical final probe/science tier.
                Assert.Equal(1, atr.Science.AcceptedFrames);
                var finalProbe = probes[^1];
                var finalTier = Number(finalProbe.GetProperty("metadata"), "exposureSeconds");
                var historical = new Queue<JsonElement>(new[] { finalProbe }.Concat(
                    recorded.Where(e => e.GetProperty("kind").GetString() == "atr-science-fits")));
                scienceReplay = new AtrWorkflowFixture(accepted, [finalTier], accepted + 4, finalTier);
                scienceReplay.Port.NextMetrics = expected => RecordedMetrics(historical.Dequeue(), expected);
                Assert.True((await scienceReplay.Science.SelectExposureAsync(scienceReplay.Context, default)).CanAdvance);
            }
            var science = await scienceReplay.Science.RunAsync(scienceReplay.Context, default);
            Assert.True(science.CanAdvance, $"{id}: {science.Gate.Message}");
            Assert.Equal(accepted, scienceReplay.Science.AcceptedFrames);
            foreach (var raw in rawHashes) Assert.Equal(raw.Sha, Hash(raw.Path));
            Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), Hash(evidencePath));
            Assert.Equal(Convert.ToHexString(SHA256.HashData(manifestBytes)), Hash(manifestPath));
            Assert.Equal(rawHash, Hash(rawPath));
            reports.Add(new { id, accepted, residuals, tolerance,
                manifestSha256 = Hash(manifestPath), evidenceSha256 = Hash(evidencePath), rawFrameSha256 = rawHash,
                strictProbe = false, warningProbeWithFullSafety = true, warningProbeWithWeakSafety = true,
                extractedPlacementCode = placement.Gate.Code, extractedScienceAccepted = scienceReplay.Science.AcceptedFrames,
                extractedProbeCaptures = atr.Probe.AttemptedFrames, reusedFinalProbes = atr.Science.ReusedProbeFrames,
                earlyHighSnrSelection, newlySelectedTier = atr.Probe.SelectedExposureSeconds,
                counterfactualScienceSuccessClaimed = false,
                replayLadder = tiers, replayScope = "recorded metrics; early selection and historical final-tier science replay are separate; fixture safety/identity; no FITS reprocessing",
                rawObservationsUnchanged = true, newAcquisition = false });
        }
        var output = Environment.GetEnvironmentVariable("UVEX_REPLAY_REPORT");
        if (!string.IsNullOrWhiteSpace(output))
            File.WriteAllText(output, JsonSerializer.Serialize(reports, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static double Number(JsonElement metadata, string key) =>
        double.Parse(metadata.GetProperty(key).GetString()!, CultureInfo.InvariantCulture);

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
