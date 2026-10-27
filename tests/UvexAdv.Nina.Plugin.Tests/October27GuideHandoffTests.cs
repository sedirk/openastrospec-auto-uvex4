using UvexAdv.Observatory;
using UvexAdv.Phd2;
using UvexAdv.Qhy.Core;
using System.Security.Cryptography;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class October27GuideHandoffTests
{
    [Theory]
    [InlineData("00143-", 800, 430, 90)]
    [InlineData("00249-", 792.0584, 414.1521, 70)]
    public void RecordedHd220526FreshGuideFrameMustNotReuseTheOldNearSlitPosition(
        string prefix, double oldX, double oldY, double minimumSlitResidual)
    {
        var root = Environment.GetEnvironmentVariable("UVEX_REPLAY_OBSERVATIONS");
        if (string.IsNullOrWhiteSpace(root)) return;
        var dir = Path.Combine(root, "UVEX-20261027T130312Z-e6899dcd323040c", "evidence");
        var path = Assert.Single(Directory.GetFiles(dir, prefix + "*.fit"));
        var before = SHA256.HashData(File.ReadAllBytes(path));
        var raw = QhyFitsCodec.Read(path);
        var frame = new MonochromeFrame(raw.Width, raw.Height, raw.Pixels,
            G3FrameInputPolicy.ResolveEffectiveSaturationAdu(raw.Pixels, 4095));
        var result = SlitTargetIdentifier.Identify(frame, StarFieldDetector.Detect(frame), new(oldX, oldY), 100, 8, 1.5);
        Assert.Equal("TARGET_IDENTIFIED_SATURATED_TOPOLOGY", result.Gate.Code);
        var point = result.Target!.Centroid;
        Assert.True(Math.Sqrt(Math.Pow(point.X - 817.6, 2) + Math.Pow(point.Y - 430, 2)) > minimumSlitResidual);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(path)));
    }

    [Fact]
    public void RecordedHd219937CoreOverlapIsNotEvidenceThatItsCentreIsOnTheSlit()
    {
        // 00265 of UVEX-20261027T124258Z-3dece03a94b7422; numerical measurements,
        // not generated pixels. All three measured centres remain below the slit.
        Phd2SlitApertureResidual?[] window = [
            new(17.862, -16.316, -7.269, 205, .5190655, 1.375, 11.5),
            new(18.910, -18.267, -4.886, 205, .5190655, 1.375, 13),
            new(18.283, -16.633, -7.590, 205, .5190655, 1.375, 11.5)];
        var policy = new ScienceQualityPolicy(true, false);
        Assert.True(policy.EvaluateSlitWarning(true, true, [17.862, 18.910, 18.283], window, 2, 3, 20).CanProbe);
        Assert.True(ScienceQualityPolicy.HasConsistentCrossSlitBias(window, 2));
        Assert.False(ScienceQualityPolicy.HasConsistentCrossSlitBias([window[0], window[1], window[2]! with { CrossSlitPixels = 7.59 }], 2));
        Assert.False(ScienceQualityPolicy.HasConsistentCrossSlitBias([window[0], window[1], null], 2));
        Assert.False(ScienceQualityPolicy.HasConsistentCrossSlitBias(window.Select(a => a! with { CrossSlitPixels = .5 }).ToArray(), 2));
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task BiasedWarningWindowGetsAtMostOneOptionalWaitAndCorrectionBeforeAtr(bool catchesLock)
    {
        var f = new Phd2ConvergenceFixture(warning: true);
        var request = f.Request([3.6, 3.6, 3.6]);
        var window = request.InitialWindow.Select((s, i) => s with { Measurement = s.Measurement with
        { TargetCentroid = new(500, 403.6), GuideStar = new(103.134, 100), CapturedUtc = f.Now.AddSeconds(i - 6) } }).ToArray();
        request = request with { InitialWindow = window, Session = request.Session with { LastMeasurement = window[^1] },
            Preset = request.Preset with { TargetOnSlitTolerancePixels = 2, MaximumGuideLockResidualPixels = 3,
                MaximumResidualGrowthPixels = 3, MaximumStagePixels = 25 } };
        var snapshot = Phd2StateSnapshot.Disconnected with { IsConnected = true, AppState = Phd2AppState.Guiding,
            ConnectionEpoch = 1, GuideEpoch = 1, LockPosition = request.Ledger.CurrentLockPosition };
        Phd2Point? dispatched = null;
        f.Trace.Handlers["get_GuideSnapshot"] = _ => snapshot;
        f.Trace.Handlers["SetExactLockPositionAsync"] = a =>
        {
            var move = (Phd2ExactLockPositionRequest)a[0]!;
            Assert.Equal(request.Ledger.AttemptsUsed + 1, Assert.Single(f.Persisted).AttemptsUsed);
            dispatched = move.DesiredPosition;
            snapshot = snapshot with { LockPosition = dispatched, GuideEpoch = 2 };
            return Task.FromResult(new Phd2ExactLockPositionResult(move.ExpectedCurrentPosition, dispatched,
                dispatched, move.MaximumStepPixels, 0, f.Now.AddSeconds(-3), true, false, false, false, false));
        };
        f.Trace.Handlers["ValidateCalibrationAsync"] = _ => Task.FromResult(request.Session.Calibration);
        f.Trace.Handlers["BeginFreshResidualObservation"] = _ => new Phd2PostLockGuidingObservation(
            "fixture", 1, 2, 9, dispatched!, 1, f.Now.AddSeconds(-3), MeasuredContinuation: true);
        f.Trace.Handlers["CapturePhd2PlacementGuideWindowAsync"] = _ =>
            Task.FromResult<IReadOnlyList<Phd2GuidingResidualState>>(window.Select((s, i) =>
            {
                var hash = new string((char)((dispatched is null ? 'D' : '7') + i), 64);
                var time = f.Now.AddSeconds(i - (dispatched is null ? 5 : 2));
                return s with { Measurement = s.Measurement with { GuideStar = dispatched ?? new(catchesLock ? 100 : 103.134, 100),
                    // Controlled optical response; not a claim of real-sky success.
                    TargetCentroid = new(500, dispatched is null ? 403.6 : 400.5), CapturedUtc = time, FrameSha256 = hash },
                    Frame = s.Frame with { Sha256 = hash, EventSequence = 10 + i, TriggerGuideFrame = 10 + i,
                        GuideStepUtc = time, CompletedUtc = time } };
            }).ToArray());
        f.Trace.Handlers["SelectPhd2CalibrationQuality"] = _ => new Phd2CalibrationCandidateSelection(
            Phd2CalibrationSelectionPurpose.LockShift, request.Session.Quality, [request.Session.Quality]);
        f.Trace.Handlers["BuildPhd2LockShiftQualification"] = _ => request.Session.Qualification;
        var result = await f.Workflow.ConvergeAsync(f.Context, request, default);
        Assert.True(result.CanAdvance, result.Gate.Message);
        Assert.Equal(catchesLock ? 1 : 0, f.Trace.Calls.Count(c => c == "SetExactLockPositionAsync"));
        Assert.Equal(catchesLock ? 2 : 1, f.Trace.Calls.Count(c => c == "CapturePhd2PlacementGuideWindowAsync"));
        Assert.Equal(request.Ledger.StartedUtc, f.Persisted[^1].StartedUtc);
        Assert.Equal(request.Ledger.AttemptsUsed + (catchesLock ? 1 : 0), f.Persisted[^1].AttemptsUsed);
        Assert.DoesNotContain("ReturnPhd2LockToOriginAsync", f.Trace.Calls);
        var atr = new AtrWorkflowFixture(1);
        Assert.True((await atr.Science.SelectExposureAsync(atr.Context, default)).CanAdvance);
        Assert.True((await atr.Science.RunAsync(atr.Context, default)).CanAdvance);
        Assert.Equal(1, atr.Science.AcceptedFrames);
    }

}
