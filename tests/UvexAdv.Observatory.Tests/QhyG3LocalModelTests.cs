using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Observatory.Tests;

public sealed class QhyG3LocalModelTests
{
    [Fact]
    public void CausalTwoTrainingPairsAndIndependentLaterHoldoutPredictCentreAndFullDetectorGeometry()
    {
        var r = Request();
        var result = QhyG3LocalModelPolicy.Evaluate(r);
        var p = Assert.IsType<QhyG3LocalPrediction>(result.Prediction);
        Assert.Equal("QHY_G3_LOCAL_MODEL_VALIDATED_PREDICTION", result.Gate.Code);
        Assert.Equal(r.Samples.Take(2).Select(s => s.Candidate.CandidateSha256), p.TrainingCandidateSha256);
        Assert.Equal(r.Samples[2].Candidate.CandidateSha256, p.HoldoutCandidateSha256);
        Assert.InRange(p.G3MinusQhyEastArcseconds, 100.4, 100.6);
        Assert.InRange(p.G3MinusQhyNorthArcseconds, -60.6, -60.4);
        Assert.InRange(p.PixelScaleArcseconds, .399, .401);
        Assert.InRange(p.PositionAngleDegrees, 254.9, 255.1);
        Assert.InRange(p.PredictionUncertaintyArcseconds, 1, 20);
        Assert.False(p.FormalSolve); Assert.False(p.MeasuredTarget);
        Assert.False(p.GlobalActiveTransfer); Assert.False(p.FineMotionAuthority);
        Assert.Equal(p.ModelSha256, QhyG3LocalModelPolicy.Evaluate(r with { Samples = r.Samples.Reverse().ToArray() }).Prediction!.ModelSha256);
    }

    [Theory]
    [InlineData("focus")] [InlineData("gs-focus")] [InlineData("gain")]
    [InlineData("preset")] [InlineData("night")] [InlineData("epoch")]
    [InlineData("pier")] [InlineData("geometry")] [InlineData("camera")]
    [InlineData("future")] [InlineData("age")] [InlineData("self")]
    [InlineData("duplicate-frame")]
    [InlineData("far-sky")] [InlineData("current-stale")] [InlineData("current-future")]
    [InlineData("too-few")] [InlineData("tampered")] [InlineData("semantics")]
    [InlineData("uncertainty")] [InlineData("same-fields")] [InlineData("long-extrapolation")]
    public void InapplicableInputsDoNotGainImageOrMovementAuthority(string fault)
    {
        var r = Request();
        var b = r.Binding;
        var samples = r.Samples.ToArray();
        var c = samples[0].Candidate;
        switch (fault)
        {
            case "focus": b = b with { OpticalState = b.OpticalState with { C11PositionSteps = 5001 } }; break;
            case "gs-focus": b = b with { OpticalState = b.OpticalState with { Gs350FocusDomainSha256 = Hash("other") } }; break;
            case "gain": b = b with { OpticalState = b.OpticalState with { G3GainPercent = 50 } }; break;
            case "preset": b = b with { CommissioningPresetSha256 = Hash("other") }; break;
            case "night": b = b with { NightSetupId = "other" }; break;
            case "epoch": b = b with { InstallationEpochId = "other" }; break;
            case "pier": b = b with { PierSide = "pierEast" }; break;
            case "geometry": b = b with { G3WidthPixels = 1919 }; break;
            case "camera": b = b with { QhyCameraId = "other" }; break;
            case "future": samples[0] = samples[0] with { Candidate = c with { CreatedUtc = r.NowUtc.AddMinutes(1) } }; break;
            case "age": r = r with { Limits = r.Limits with { MaximumSampleAge = TimeSpan.FromMinutes(2) } }; break;
            case "self": r = r with { CurrentQhy = r.CurrentQhy with { FrameSha256 = c.Qhy.FrameSha256 } }; break;
            case "duplicate-frame": samples[1] = samples[1] with { Candidate = Rebuild(samples[1].Candidate with { Qhy = samples[1].Candidate.Qhy with { FrameSha256 = c.Qhy.FrameSha256 } }) }; break;
            case "far-sky": r = r with { CurrentQhy = r.CurrentQhy with { CenterRightAscensionDegrees = 40 } }; break;
            case "current-stale": r = r with { NowUtc = r.NowUtc.AddMinutes(2) }; break;
            case "current-future": r = r with { CurrentQhy = r.CurrentQhy with { SolveCompletedUtc = r.NowUtc.AddSeconds(1) } }; break;
            case "too-few": samples = samples[..2]; break;
            case "tampered": samples[0] = samples[0] with { Candidate = c with { CandidateSha256 = Hash("changed") } }; break;
            case "semantics":
                c = c with { Model = c.Model with { G3MinusQhyEastArcseconds = 99 } };
                samples[0] = samples[0] with { Candidate = c with { CandidateSha256 = c.ComputeCandidateSha256() } }; break;
            case "uncertainty": r = r with { Limits = r.Limits with { MaximumPredictionUncertaintyArcseconds = .1 } }; break;
            case "same-fields":
                samples = samples.Select(s => s with { Candidate = Rebuild(s.Candidate with {
                    Qhy = s.Candidate.Qhy with { CenterRightAscensionDegrees = 350 },
                    G3 = s.Candidate.G3 with { CenterRightAscensionDegrees = 350.03 } }) }).ToArray(); break;
            case "long-extrapolation":
                var next = r.NowUtc.AddMinutes(30);
                r = r with { NowUtc = next, CurrentQhy = Shift(r.CurrentQhy, TimeSpan.FromMinutes(30)) }; break;
        }
        var result = QhyG3LocalModelPolicy.Evaluate(r with { Binding = b, Samples = samples });
        Assert.Null(result.Prediction);
        Assert.StartsWith("QHY_G3_LOCAL_MODEL_", result.Gate.Code);
        Assert.Equal(GateDisposition.Passed, result.Gate.Disposition); // Optional miss is visible warning, not an added night blocker.
    }

    [Fact]
    public void DifferentImmutableSameRunExposuresValidateLocalRepeatabilityWithoutThreeSuccessfulTargets()
    {
        var r = Request();
        var samples = r.Samples.Select(s => {
            var c = s.Candidate; var q = c.Qhy with { CenterRightAscensionDegrees = 350 };
            var point = G3AcquisitionMotionPlanner.ApplyTangentOffsetArcseconds(350, q.CenterDeclinationDegrees,
                c.Model.G3MinusQhyEastArcseconds, c.Model.G3MinusQhyNorthArcseconds);
            return s with { Candidate = Rebuild(c with { ObservationRunId = "same-target-run", Qhy = q,
                G3 = c.G3 with { CenterRightAscensionDegrees = point.RaDegrees, CenterDeclinationDegrees = point.DecDegrees } }) };
        }).ToArray();
        var result = QhyG3LocalModelPolicy.Evaluate(r with { Samples = samples,
            CurrentQhy = r.CurrentQhy with { CenterRightAscensionDegrees = 350.001 } });
        Assert.NotNull(result.Prediction);
        Assert.Equal(samples[^1].Candidate.CandidateSha256, result.Prediction.HoldoutCandidateSha256);
        Assert.True(QhyG3LocalModelPolicy.HasIndependentFitSupport(samples, r.Binding, 20));
    }

    [Fact]
    public void QueryLocalitySelectsBeforeFitAndSinglePairCanOnlySupplySearchPrior()
    {
        var r = Request();
        var far = new QhyG3VerifiedLocalSample(Candidate(9, r.Samples[0].Candidate.CreatedUtc),
            r.Binding.OpticalState, "far-focus.json", Hash("far-focus"));
        var selected = QhyG3LocalModelPolicy.SelectLocalSamples(r.Samples.Append(far).ToArray(), r.CurrentQhy,
            r.NowUtc, r.Limits.MaximumSampleAge);
        Assert.Equal(3, selected.Count);
        Assert.DoesNotContain(selected, s => s.Candidate.CandidateSha256 == far.Candidate.CandidateSha256);
        Assert.NotNull(QhyG3LocalModelPolicy.Evaluate(r with { Samples = selected }).Prediction);
        var single = r with { Samples = r.Samples.Take(1).ToArray() };
        Assert.Null(QhyG3LocalModelPolicy.Evaluate(single).Prediction);
        var prior = Assert.IsType<QhyG3LocalSearchPrior>(QhyG3LocalModelPolicy.SearchPrior(single));
        Assert.False(prior.MotionAuthority); Assert.False(prior.MeasuredTarget);
        Assert.Null(QhyG3LocalModelPolicy.SearchPrior(single with { Binding = r.Binding with {
            OpticalState = r.Binding.OpticalState with { C11PositionSteps = 5001 } } }));
    }

    [Theory]
    [InlineData("mount")] [InlineData("qhy")] [InlineData("g3")]
    [InlineData("qhy-path")] [InlineData("g3-path")] [InlineData("night-id")]
    [InlineData("bin")] [InlineData("parity")] [InlineData("focus-proof")]
    [InlineData("semantics")]
    public void ColdSearchPriorIndependentlyRejectsWrongIdentityGeometryAndRehashedFakeModel(string fault)
    {
        var r = Request() with { Samples = Request().Samples.Take(1).ToArray() };
        var b = r.Binding;
        switch (fault)
        {
            case "mount": b = b with { TelescopeId = "wrong-mount" }; break;
            case "qhy": b = b with { QhyCameraId = "wrong-camera" }; break;
            case "g3": b = b with { G3CameraId = "wrong-camera" }; break;
            case "qhy-path": b = b with { QhyOpticalTrainId = "wrong-path" }; break;
            case "g3-path": b = b with { G3OpticalTrainId = "wrong-path" }; break;
            case "night-id": b = b with { NightSetupId = "wrong-night" }; break;
            case "bin": b = b with { G3Binning = 2 }; break;
            case "parity": b = b with { ExpectedG3Flipped = true }; break;
            case "focus-proof": r = r with { Samples = new[] { r.Samples[0] with { FocusEvidenceSha256 = "" } } }; break;
            case "semantics":
                var c = r.Samples[0].Candidate;
                c = c with { Model = c.Model with { G3MinusQhyEastArcseconds = 400 } };
                r = r with { Samples = new[] { r.Samples[0] with { Candidate = c with { CandidateSha256 = c.ComputeCandidateSha256() } } } }; break;
        }
        Assert.Null(QhyG3LocalModelPolicy.SearchPrior(r with { Binding = b }));
    }

    [Theory]
    [InlineData("translation")] [InlineData("rotation")] [InlineData("scale")] [InlineData("parity")]
    public void DiscrepantIndependentHoldoutInvalidatesWholeFitRatherThanSelectingItAway(string fault)
    {
        var r = Request();
        var samples = r.Samples.ToArray();
        var c = samples[^1].Candidate;
        c = fault switch
        {
            "translation" => c with { G3 = c.G3 with { CenterRightAscensionDegrees = c.G3.CenterRightAscensionDegrees + .1 } },
            "rotation" => c with { G3 = c.G3 with { PositionAngleDegrees = c.G3.PositionAngleDegrees + 5 } },
            "scale" => c with { G3 = c.G3 with { PixelScaleArcseconds = .5 } },
            _ => c with { G3 = c.G3 with { Flipped = true } },
        };
        samples[^1] = samples[^1] with { Candidate = Rebuild(c) };
        Assert.Null(QhyG3LocalModelPolicy.Evaluate(r with { Samples = samples }).Prediction);
    }

    [Theory]
    [InlineData(31, 5, 20)] [InlineData(30, 6, 20)] [InlineData(30, 5, double.NaN)]
    public void PolicyCannotExpandMaximumTimeOrSkyDomain(int minutes, double degrees, double uncertainty) =>
        Assert.Null(QhyG3LocalModelPolicy.Evaluate(Request() with { Limits = new(TimeSpan.FromMinutes(minutes),
            TimeSpan.FromSeconds(15), degrees, uncertainty) }).Prediction);

    private static QhyG3LocalModelRequest Request()
    {
        var t = new DateTimeOffset(2026, 10, 24, 0, 0, 0, TimeSpan.Zero);
        var state = new QhyG3LocalOpticalState("C11-focuser", 5000, Hash("gs-focus-domain"), null, "ExplicitManualLock", 100);
        var samples = Enumerable.Range(0, 3).Select(i => new QhyG3VerifiedLocalSample(Candidate(i, t.AddMinutes(i * 5)), state,
            $"focus-{i}.json", Hash("focus-" + i))).ToArray();
        var c = samples[0].Candidate;
        var binding = new QhyG3LocalModelBinding(c.CommissioningPresetSha256, c.NightSetupId, c.NightSetupSha256,
            c.InstallationEpochId, c.TelescopeDeviceId, c.QhyCameraStableId, c.G3CameraStableId, c.QhyOpticalTrainId,
            c.G3OpticalTrainId, c.PierSide, 1920, 1080, 1, state);
        var current = Shift(samples[^1].Candidate.Qhy, TimeSpan.FromMinutes(5)) with {
            FrameSha256 = Hash("current"), CenterRightAscensionDegrees = 352.5 };
        return new(binding, current, samples, new(TimeSpan.FromMinutes(30), TimeSpan.FromSeconds(15), 5, 20), t.AddMinutes(15).AddSeconds(8));
    }

    private static QhyToG3TransferCandidate Candidate(int i, DateTimeOffset t)
    {
        var q = new QhyG3SolvedFrame("QHY", "qhy", $"q{i}.fit", Hash($"q{i}"), $"q{i}.json", Hash($"qs{i}"),
            t.AddSeconds(3), t.AddSeconds(4), t.AddSeconds(5), t.AddSeconds(6), "QHY native exposure",
            3856, 2180, 1, 1, 0, 0, 3856, 2180, 350 + i, 30, 2, 270, false, Hash($"qb{i}"));
        var gCenter = G3AcquisitionMotionPlanner.ApplyTangentOffsetArcseconds(q.CenterRightAscensionDegrees, q.CenterDeclinationDegrees, 100 + i, -60 - i);
        var g = new QhyG3SolvedFrame("G3", "g3", $"g{i}.fit", Hash($"g{i}"), $"g{i}.json", Hash($"gs{i}"),
            t, t.AddSeconds(1), t.AddSeconds(2), t.AddSeconds(3), "PHD2 native exposure",
            1920, 1080, 1, 1, 0, 0, 1920, 1080, gCenter.RaDegrees, gCenter.DecDegrees, .4, 255, false, Hash($"gb{i}"));
        var readbacks = new[] {
            new QhyG3PairMountReadback("g3-before-exposure", 350 + i, 30, "JNOW", "pierWest", t),
            new QhyG3PairMountReadback("g3-after-exposure", 350 + i, 30, "JNOW", "pierWest", t.AddSeconds(2)),
            new QhyG3PairMountReadback("qhy-before-job", 350 + i, 30, "JNOW", "pierWest", t.AddSeconds(3)),
            new QhyG3PairMountReadback("qhy-after-accepted-frame", 350 + i, 30, "JNOW", "pierWest", t.AddSeconds(5)),
            new QhyG3PairMountReadback("pair-final-readback", 350 + i, 30, "JNOW", "pierWest", t.AddSeconds(7)) };
        return Assert.IsType<QhyToG3TransferCandidate>(QhyG3SolvePairBuilder.Build(new(
            new(1, "test", true, 2, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30),
                2, TimeSpan.FromHours(1), 20), "run-" + i, Hash("action-" + i), Hash("preset"), "night", Hash("night"),
            "installation", "mount", "GS350/qhy", "C11/g3", QhyG3SolvePairSource.ImmediateSingleQhyExposure,
            q, g, readbacks, t.AddSeconds(7))).Candidate);
    }

    private static QhyToG3TransferCandidate Rebuild(QhyToG3TransferCandidate c) => Assert.IsType<QhyToG3TransferCandidate>(
        QhyG3SolvePairBuilder.Build(new(c.CollectionPolicy, c.ObservationRunId, c.ActionConfigurationSha256,
            c.CommissioningPresetSha256, c.NightSetupId, c.NightSetupSha256, c.InstallationEpochId, c.TelescopeDeviceId,
            c.QhyOpticalTrainId, c.G3OpticalTrainId, c.PairSource, c.Qhy, c.G3, c.MountReadbacks, c.CreatedUtc)).Candidate);
    private static QhyG3SolvedFrame Shift(QhyG3SolvedFrame f, TimeSpan delta) => f with {
        ExposureStartedUtc = f.ExposureStartedUtc + delta, ExposureMidpointUtc = f.ExposureMidpointUtc + delta,
        ExposureEndedUtc = f.ExposureEndedUtc + delta, SolveCompletedUtc = f.SolveCompletedUtc + delta };
    private static string Hash(string value) => QhyG3LocalModelPolicy.HashFields(value);
}
