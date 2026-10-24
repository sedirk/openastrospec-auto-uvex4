using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class RecordedQhyG3LocalModelTests
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
    [Fact]
    public void October23PastPairsPredictFourthFieldWithIndependentHoldoutAndExplicitAuditFocusReconstruction()
    {
        var root = Environment.GetEnvironmentVariable("UVEX_REPLAY_OBSERVATIONS");
        if (string.IsNullOrWhiteSpace(root)) return;
        var runs = new[] { "UVEX-20261023T123550Z-ed9c198f39b64e7", "UVEX-20261023T124609Z-4f199a0a5f07404",
            "UVEX-20261023T124924Z-79c20faecdee447", "UVEX-20261023T125614Z-cdae286312eb469" };
        var records = runs.Select(run => ReadPair(root, run)).ToArray();
        var auditPath = Path.Combine(Directory.GetParent(root)!.FullName, "closed-loop", "2026-10-23.jsonl");
        var auditBefore = Digest(auditPath);
        var setupPath = Path.Combine(Directory.GetParent(root)!.FullName, "commissioning",
            "night-focus-recovery-20261021T145909Z-d0dd7d8d", "night-setup.json");
        Assert.Equal(records[0].NightSetupSha256, Digest(setupPath), ignoreCase: true);
        using var setup = JsonDocument.Parse(File.ReadAllText(setupPath));
        var gs = setup.RootElement.GetProperty("FocusDomains").EnumerateArray().Single(d => d.GetProperty("Role").GetString() == "Gs350WideField");
        Assert.Equal("ManualOperator", gs.GetProperty("Owner").GetString());
        Assert.Equal(0, gs.GetProperty("Limits").GetProperty("MaximumCumulativeMoveSteps").GetInt32());
        var gsSha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(gs.GetRawText())));
        var audit = File.ReadLines(auditPath).Select(line => JsonDocument.Parse(line)).ToArray();
        try
        {
            var opticalStates = records.Select(c =>
            {
                var last = audit.Where(d => d.RootElement.GetProperty("kind").GetString() == "night-setup-revalidated" &&
                    d.RootElement.GetProperty("timestampUtc").GetDateTimeOffset() <= c.CreatedUtc).Last().RootElement.GetProperty("result");
                Assert.Equal(c.ActionConfigurationSha256, last.GetProperty("ActionConfigurationSha256").GetString(), ignoreCase: true);
                var position = last.GetProperty("c11RunStartPositionSteps").GetInt32();
                Assert.Equal(4800, position); // A same-preset manual focus change from historical 5000 really occurred.
                return new QhyG3LocalOpticalState(last.GetProperty("c11FocuserDeviceId").GetString()!, position,
                    gsSha, null, "ExplicitManualLock", 100);
            }).ToArray();
            Assert.Single(opticalStates.Select(s => s.ComputeSha256()).Distinct());
            // This explicitly reconstructed fixture is NOT saved into production schema-2 archives.
            var samples = records.Take(3).Select((c, i) => new QhyG3VerifiedLocalSample(c, opticalStates[i], auditPath, auditBefore)).ToArray();
            var current = records[3];
            var binding = QhyG3LocalModelArchive.BindingFor(current, opticalStates[3]);
            var request = new QhyG3LocalModelRequest(binding, current.Qhy, samples,
                new(TimeSpan.FromMinutes(30), TimeSpan.FromSeconds(15), 5, 100 * current.G3.PixelScaleArcseconds), current.CreatedUtc);
            var decision = QhyG3LocalModelPolicy.Evaluate(request);
            Assert.True(QhyG3LocalModelPolicy.HasIndependentFitSupport(samples, binding, request.Limits.MaximumPredictionUncertaintyArcseconds));
            var p = Assert.IsType<QhyG3LocalPrediction>(decision.Prediction);
            Assert.Equal(samples[2].Candidate.CandidateSha256, p.HoldoutCandidateSha256);
            Assert.DoesNotContain(current.CandidateSha256, p.TrainingCandidateSha256);
            var actualError = G3AcquisitionMotionPlanner.AngularSeparationArcseconds(p.RightAscensionDegrees, p.DeclinationDegrees,
                current.G3.CenterRightAscensionDegrees, current.G3.CenterDeclinationDegrees);
            Assert.InRange(actualError, 0, p.PredictionUncertaintyArcseconds);
            Assert.InRange(p.PredictionUncertaintyArcseconds / current.G3.PixelScaleArcseconds, 1, 100);
            Assert.Null(QhyG3LocalModelPolicy.Evaluate(request with { Samples = samples.Append(new(current, opticalStates[3], auditPath, auditBefore)).ToArray() }).Prediction);
            var old = ReadPair(root, "UVEX-20261014T112329Z-1862faa86ec84de");
            Assert.Null(QhyG3LocalModelPolicy.Evaluate(request with { Samples = new[] {
                samples[0] with { Candidate = old }, samples[1], samples[2] } }).Prediction);
            Assert.Null(QhyG3LocalModelPolicy.Evaluate(request with { Binding = binding with {
                OpticalState = opticalStates[3] with { C11PositionSteps = 5000 } } }).Prediction);
            // The historical per-target QHY samples are minutes/apart pointings:
            // do not fabricate a short same-pointing optical drift witness from them.
            var historicalDrift = QhyG3SkyDriftPolicy.Build(records[2].Qhy, current.Qhy, 3, 3,
                current.G3.ExposureStartedUtc, current.G3.ExposureEndedUtc, 5000, current.CollectionPolicy,
                current.CreatedUtc, request.Limits.MaximumPredictionUncertaintyArcseconds - p.PredictionUncertaintyArcseconds);
            Assert.Null(historicalDrift.Witness);
            var output = Environment.GetEnvironmentVariable("UVEX_LOCAL_DUAL_REPLAY_REPORT");
            if (!string.IsNullOrWhiteSpace(output))
                File.WriteAllText(output, JsonSerializer.Serialize(new { causal = true,
                    fixture = "historical-audit-reconstruction-not-production-autoload", currentRun = current.ObservationRunId,
                    sourceRunIds = samples.Select(s => s.Candidate.ObservationRunId), decision,
                    actualErrorArcseconds = actualError, actualErrorPixels = actualError / current.G3.PixelScaleArcseconds,
                    uncertaintyPixels = p.PredictionUncertaintyArcseconds / current.G3.PixelScaleArcseconds,
                    auditedC11PositionSteps = opticalStates[3].C11PositionSteps, auditSha256 = auditBefore,
                    historicalDrift, twoFreshSamePointingQhyFramesWereNotRecorded = true,
                    nativeCurrentFieldSucceeded = true, noNewHardwareAcquisition = true }, new JsonSerializerOptions { WriteIndented = true }));
            foreach (var c in records.Append(old)) VerifySourceHashes(c);
            Assert.Equal(auditBefore, Digest(auditPath));
        }
        finally { foreach (var d in audit) d.Dispose(); }
    }

    private static QhyToG3TransferCandidate ReadPair(string root, string run)
    {
        var path = Assert.Single(Directory.GetFiles(Path.Combine(root, run, "evidence"), "*-qhy-g3-fast-solve-pair.json"));
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var c = Assert.IsType<QhyToG3TransferCandidate>(doc.RootElement.GetProperty("payload").GetProperty("candidate")
            .Deserialize<QhyToG3TransferCandidate>(Options));
        Assert.Empty(c.ValidateIntegrity());
        VerifySourceHashes(c);
        return c;
    }
    private static void VerifySourceHashes(QhyToG3TransferCandidate c)
    {
        foreach (var f in new[] { c.Qhy, c.G3 })
        {
            Assert.Equal(f.FrameSha256, Digest(f.FramePath), ignoreCase: true);
            Assert.Equal(f.SolveEvidenceSha256, Digest(f.SolveEvidencePath), ignoreCase: true);
        }
    }
    private static string Digest(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
}
