using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class G3SlitIlluminationPolicyTests
{
    [Fact]
    public void ThreeFrameMedianRejectsOneLedTransitionFrame()
    {
        var stableA = Frame(100, 200, 300, 400, 500, 600, 700, 800, 900);
        var transition = Frame(40_000, 50_000, 60_000, 65_000, 61_000, 55_000, 48_000, 44_000, 42_000);
        var stableB = Frame(102, 198, 301, 399, 502, 598, 701, 799, 903);

        var composite = G3SlitIlluminationPolicy.MedianComposite(
            [stableA, transition, stableB]);

        Assert.Equal((ushort)102, composite[0, 0]);
        Assert.Equal((ushort)200, composite[1, 0]);
        Assert.Equal((ushort)400, composite[0, 1]);
        Assert.Equal((ushort)502, composite[1, 1]);
    }

    [Fact]
    public void LowConfidencePassingGeometryIsConvertedToNeedsAttention()
    {
        var geometry = new SlitGeometry(
            "seed",
            new PixelPoint(20, 20),
            90,
            30,
            4,
            1,
            "G3",
            1,
            1);
        var analysis = new SlitIlluminationPairAnalysis(
            GateResult.Pass("MEASURED", "synthetic"),
            geometry,
            SlitIlluminationPolarity.Bright,
            8,
            0,
            0,
            4,
            Confidence: 0.20,
            UniquenessRatio: 1.5,
            ValidFraction: 1,
            SaturatedFraction: 0,
            BadPixelFraction: 0);

        var gated = G3SlitIlluminationPolicy.ApplyConfidenceGate(analysis);

        Assert.Equal(GateDisposition.Indeterminate, gated.Gate.Disposition);
        Assert.Equal("SLIT_LED_PAIR_LOW_CONFIDENCE", gated.Gate.Code);
        Assert.Equal(analysis.Geometry, gated.Geometry);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task PauseRequestedDuringOnBlockReachesWaitOnlyAfterOff(int pauseAfterFrame)
    {
        var events = new List<string>();
        var pauseRequested = false;

        await G3AtomicLedOnBlock.ExecuteAsync(
            frameCount: 3,
            maximumOnDuration: TimeSpan.FromSeconds(2),
            offTimeout: TimeSpan.FromSeconds(1),
            turnOn: _ =>
            {
                events.Add("on");
                return Task.CompletedTask;
            },
            captureFrame: (index, _) =>
            {
                events.Add($"frame-{index}");
                if (index == pauseAfterFrame) pauseRequested = true;
                return Task.CompletedTask;
            },
            turnOff: _ =>
            {
                events.Add("off");
                return Task.CompletedTask;
            },
            checkpointAfterOff: _ =>
            {
                Assert.True(pauseRequested);
                events.Add("pause-wait");
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(
            new[] { "on", "frame-1", "frame-2", "frame-3", "off", "pause-wait" },
            events);
    }

    [Fact]
    public async Task CancellationAfterOn1StillTurnsOffBeforeRethrow()
    {
        using var cancellation = new CancellationTokenSource();
        var events = new List<string>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            G3AtomicLedOnBlock.ExecuteAsync(
                frameCount: 3,
                maximumOnDuration: TimeSpan.FromSeconds(2),
                offTimeout: TimeSpan.FromSeconds(1),
                turnOn: _ =>
                {
                    events.Add("on");
                    return Task.CompletedTask;
                },
                captureFrame: (index, _) =>
                {
                    events.Add($"frame-{index}");
                    if (index == 1) cancellation.Cancel();
                    return Task.CompletedTask;
                },
                turnOff: _ =>
                {
                    events.Add("off");
                    return Task.CompletedTask;
                },
                checkpointAfterOff: _ =>
                {
                    events.Add("pause-wait");
                    return Task.CompletedTask;
                },
                cancellation.Token));

        Assert.Equal(new[] { "on", "frame-1", "off" }, events);
    }

    [Fact]
    public async Task CaptureExceptionAfterOn2StillTurnsOffBeforeRethrow()
    {
        var events = new List<string>();
        var exception = await Assert.ThrowsAsync<IOException>(() =>
            G3AtomicLedOnBlock.ExecuteAsync(
                frameCount: 3,
                maximumOnDuration: TimeSpan.FromSeconds(2),
                offTimeout: TimeSpan.FromSeconds(1),
                turnOn: _ =>
                {
                    events.Add("on");
                    return Task.CompletedTask;
                },
                captureFrame: (index, _) =>
                {
                    events.Add($"frame-{index}");
                    return index == 2
                        ? Task.FromException(new IOException("synthetic capture failure"))
                        : Task.CompletedTask;
                },
                turnOff: _ =>
                {
                    events.Add("off");
                    return Task.CompletedTask;
                },
                checkpointAfterOff: _ =>
                {
                    events.Add("pause-wait");
                    return Task.CompletedTask;
                },
                CancellationToken.None));

        Assert.Equal("synthetic capture failure", exception.Message);
        Assert.Equal(new[] { "on", "frame-1", "frame-2", "off" }, events);
    }

    [Fact]
    public void RealRunnerKeepsLedOnFramesAtomicAndAlwaysHasFinallyOff()
    {
        var sourcePath = Path.Combine(
            AppContext.BaseDirectory,
            "Sources",
            "RealObservationStageRunner.cs");
        var source = File.ReadAllText(sourcePath);
        var phaseBody = Slice(
            source,
            "private async Task CaptureG3IlluminationPhaseAsync(",
            "private GateResult ValidateG3SequenceImage(");
        var sequenceBody = Slice(
            source,
            "private async Task<G3SlitIlluminationSequence> CaptureG3SlitIlluminationSequenceAsync(",
            "private async Task CaptureG3IlluminationPhaseAsync(");

        // The ordinary phase loop is restricted to OFF. ON is delegated to the
        // unit-tested atomic block, which proves SLOF before its pause checkpoint.
        Assert.Equal(1, Count(phaseBody, "RequireImmediatePhysicalActionGatesAsync"));
        Assert.Contains("cooperativePauseCheckpoint", phaseBody, StringComparison.Ordinal);
        Assert.Contains("G3AtomicLedOnBlock.ExecuteAsync", sequenceBody, StringComparison.Ordinal);

        var onCommand = sequenceBody.IndexOf("enabled: true", StringComparison.Ordinal);
        var onCapture = sequenceBody.IndexOf("G3SlitIlluminationPhase.On", onCommand, StringComparison.Ordinal);
        var offAfterCommand = sequenceBody.IndexOf("\"off-after\"", onCapture, StringComparison.Ordinal);
        var finallyOff = sequenceBody.IndexOf("EnsureSlitIlluminationOffAsync", offAfterCommand, StringComparison.Ordinal);
        Assert.True(onCommand >= 0 && onCapture > onCommand && offAfterCommand > onCapture && finallyOff > offAfterCommand);
        Assert.Contains("new CancellationTokenSource(TimeSpan.FromSeconds(20))", sequenceBody, StringComparison.Ordinal);
    }

    [Fact]
    public void RealRunnerUsesHdrDarkApertureAnalyzerAndNeverFallsBackToBrightRidgeWidth()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "Sources",
            "RealObservationStageRunner.cs"));

        Assert.Contains("SlitDarkApertureHdrAnalyzer.Analyze", source, StringComparison.Ordinal);
        Assert.Contains("reflected-ridge FWHM is diagnostic only", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SlitLocusDetector.DetectDarkSlit", source, StringComparison.Ordinal);
        Assert.Contains("detector-fixed per-pixel median", source, StringComparison.Ordinal);
        Assert.Contains("G3_MAIN_FOCUS_UNVERIFIED", source, StringComparison.Ordinal);

        var g3AnalysisBody = Slice(
            source,
            "private async Task<G3FieldState> CaptureAndAnalyzeG3Async(",
            "private async Task<G3SlitIlluminationSequence> CaptureG3SlitIlluminationSequenceAsync(");
        Assert.DoesNotContain("MoveFocusAsync", g3AnalysisBody, StringComparison.Ordinal);
        Assert.DoesNotContain("MoveGratingAsync", g3AnalysisBody, StringComparison.Ordinal);
        Assert.DoesNotContain("NinaClosedLoop", g3AnalysisBody, StringComparison.Ordinal);
    }

    [Fact]
    public void RealRunnerOpticallyVerifiesSlitIdentityBeforeWcsOrMotionAuthority()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "Sources",
            "RealObservationStageRunner.cs"));
        var body = Slice(
            source,
            "private async Task<G3FieldState> CaptureAndAnalyzeG3Async(",
            "private static bool FocusFailureMayBeSaturationDominated(");

        var pairedAnalysis = body.IndexOf("SlitDarkApertureHdrAnalyzer.Analyze", StringComparison.Ordinal);
        var identityMatch = body.IndexOf("SlitWheelIdentityMatcher.Match", StringComparison.Ordinal);
        var identityBlock = body.IndexOf("slitIdentity.Gate.Disposition != GateDisposition.Passed", StringComparison.Ordinal);
        var plateSolve = body.IndexOf("SolveImageAsync", StringComparison.Ordinal);

        Assert.True(pairedAnalysis >= 0 && identityMatch > pairedAnalysis);
        Assert.True(identityBlock > identityMatch && plateSolve > identityBlock);
        Assert.Contains("PublishSlitWheelIdentityEvidenceAsync", body, StringComparison.Ordinal);
        Assert.Contains("SLIT_LED_IDENTITY", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RealRunnerEvidenceRequiresTwoNineFrameHdrSequencesAndPersistsLedTelemetry()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "Sources",
            "RealObservationStageRunner.cs"));
        var sequenceBody = Slice(
            source,
            "private async Task<G3SlitIlluminationSequence> CaptureG3SlitIlluminationSequenceAsync(",
            "private async Task CaptureG3IlluminationPhaseAsync(");
        var evidenceBody = Slice(
            source,
            "private async Task PublishG3SlitSequenceEvidenceAsync(",
            "private static string PhaseEvidenceName(");

        Assert.Contains("hashedFrames.Count == G3SlitIlluminationPolicy.FramesPerPhase * 3", sequenceBody, StringComparison.Ordinal);
        Assert.Contains("ShortExposureMilliseconds", source, StringComparison.Ordinal);
        Assert.Contains("LongExposureMilliseconds", source, StringComparison.Ordinal);
        Assert.Contains("hashedFrames.All(frame => !string.IsNullOrWhiteSpace(frame.Sha256))", sequenceBody, StringComparison.Ordinal);
        Assert.Contains("command.CommandedUtc", evidenceBody, StringComparison.Ordinal);
        Assert.Contains("command.SlitPhotodiodeValue", evidenceBody, StringComparison.Ordinal);
        Assert.Contains("command.SlitPhotodiodeThreshold", evidenceBody, StringComparison.Ordinal);
        Assert.Contains("command.SlitPhotodiodeEnabled", evidenceBody, StringComparison.Ordinal);
        Assert.Contains("frame.Sha256", evidenceBody, StringComparison.Ordinal);
    }

    [Fact]
    public void EverySlitIlluminationFrameIsPublishedImmediatelyAfterCapture()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "Sources",
            "RealObservationStageRunner.cs"));
        var body = Slice(
            source,
            "private async Task CaptureG3IlluminationFrameAsync(",
            "private GateResult ValidateG3SequenceImage(");

        var capture = body.IndexOf("phd2.CaptureFullFrameAsync", StringComparison.Ordinal);
        var load = body.IndexOf("imageDataFactory.CreateFromFile", capture, StringComparison.Ordinal);
        var preview = body.IndexOf("PublishG3Preview", load, StringComparison.Ordinal);
        var recoveryDelay = body.IndexOf("Task.Delay", preview, StringComparison.Ordinal);

        Assert.True(capture >= 0 && load > capture && preview > load && recoveryDelay > preview);
        Assert.Contains("· 已保存。", body, StringComparison.Ordinal);
    }

    [Fact]
    public void RealRunnerPacesRepeatedToupTekFullFrameCapturesWithHashBoundSetting()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "Sources",
            "RealObservationStageRunner.cs"));
        var captureBody = Slice(
            source,
            "private async Task CaptureG3IlluminationFrameAsync(",
            "private GateResult ValidateG3SequenceImage(");

        var capture = captureBody.IndexOf("CaptureFullFrameAsync", StringComparison.Ordinal);
        var delay = captureBody.IndexOf("configuration.G3.CameraRecoveryDelayMilliseconds", StringComparison.Ordinal);
        Assert.True(capture >= 0 && delay > capture);
        Assert.Contains("index < G3SlitIlluminationPolicy.FramesPerPhase", captureBody, StringComparison.Ordinal);
        Assert.Contains("cameraRecoveryDelayMilliseconds = configuration.G3.CameraRecoveryDelayMilliseconds", source, StringComparison.Ordinal);
    }

    private static MonochromeFrame Frame(params ushort[] pixels) => new(3, 3, new ReadOnlyMemory<ushort>(pixels), ushort.MaxValue);

    private static string Slice(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, $"Could not find source slice {startMarker} ... {endMarker}.");
        return source[start..end];
    }

    private static int Count(string source, string value)
    {
        var count = 0;
        var offset = 0;
        while ((offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }
        return count;
    }
}
