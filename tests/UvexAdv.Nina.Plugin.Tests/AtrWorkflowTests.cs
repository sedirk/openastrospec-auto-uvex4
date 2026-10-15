using NINA.Image.Interfaces;
using UvexAdv.Observatory;
using UvexAdv.Spectroscopy;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class AtrWorkflowTests
{
    [Theory]
    [InlineData(1)] [InlineData(3)]
    public async Task FinalProbeIsCreditedOnceAndOnlyMissingScienceFramesAreCaptured(int count)
    {
        var f = new AtrWorkflowFixture(count);
        Assert.True((await f.Science.SelectExposureAsync(f.Context, default)).CanAdvance);
        var capture = Assert.Single(f.Port.Captures);
        var saved = Assert.Single(f.Port.Saved);
        await f.Science.CreditFinalProbeAsync(f.Context, capture, saved, 30, default);
        Assert.Equal(1, f.Science.AcceptedFrames);
        Assert.True((await f.Science.RunAsync(f.Context, default)).CanAdvance);
        Assert.Equal(count, f.Science.AcceptedFrames);
        Assert.Equal(count, f.Science.AttemptedFrames);
        Assert.Equal(count, f.Port.Captures.Count);
        Assert.Equal(1, f.Science.ReusedProbeFrames);
        Assert.Equal(count - 1, f.Science.RetainedFrames);
        Assert.Equal(count * 30, f.Science.AcceptedExposureSeconds);
        Assert.Single(f.Port.ScienceReferences);
        Assert.Equal(saved.Path, f.Port.ScienceReferences[0]); // reference, not another save
        var before = f.Port.Captures.Count;
        await f.Science.RunAsync(f.Context, default);
        Assert.Equal(before, f.Port.Captures.Count);
    }

    [Fact]
    public async Task FaintTargetAdvancesOneTierThenContinuesScienceWithWarnings()
    {
        var f = new AtrWorkflowFixture(3, [30, 60, 120]);
        f.Port.NextMetrics = m => m with { TargetToSkyContrast = .5 };
        var selected = await f.Science.SelectExposureAsync(f.Context, default);
        Assert.Equal("ATR_SIGNAL_LIMITED_LONGEST_SAFE_TIER", selected.Gate.Code);
        Assert.True(selected.CanAdvance);
        Assert.Equal(new double[] { 30, 60, 120 }, f.Port.Captures.Select(c => c.Metrics.ExposureSeconds));
        var result = await f.Science.RunAsync(f.Context, default);
        Assert.True(result.CanAdvance);
        Assert.Equal(GateSeverity.Warning, result.Gate.Severity);
        Assert.Equal(3, f.Probe.AttemptedFrames);
        Assert.Equal(3, f.Science.AcceptedFrames);
        Assert.Equal(3, f.Science.WarningFrames);
        Assert.Equal(360, f.Science.AcceptedExposureSeconds);
    }

    [Fact]
    public async Task ClippedLongProbeBacksOffToOneFreshFrameWithoutReusingOldValidation()
    {
        var f = new AtrWorkflowFixture(1, [30, 60]);
        var peaks = new Queue<double>([20000, 65000, 30000]);
        f.Port.NextMetrics = m => m with { HighPercentileAdu = peaks.Dequeue(),
            ContinuumSnrPerResolutionElement = peaks.Count == 2 ? 1 : 20,
            LineSnrPerResolutionElement = peaks.Count == 2 ? 1 : 30 };
        Assert.True((await f.Science.SelectExposureAsync(f.Context, default)).CanAdvance);
        Assert.Equal(new double[] { 30, 60, 30 }, f.Port.Captures.Select(c => c.Metrics.ExposureSeconds));
        Assert.Equal(3, f.Probe.RetainedFrames);
        Assert.Equal(1, f.Science.AcceptedFrames);
        Assert.Equal(f.Port.Saved[2].Path, Assert.Single(f.Port.ScienceReferences));
    }

    [Fact]
    public async Task RecordedHd23552GoodThreeHundredSecondProbeIsCreditedWithoutChasingSixHundred()
    {
        var f = new AtrWorkflowFixture(2, [30, 300, 600]);
        f.Port.NextMetrics = m => m.ExposureSeconds == 30
            ? m with { HighPercentileAdu = 3000, ContinuumSnrPerResolutionElement = 1, LineSnrPerResolutionElement = 2 }
            : m with { HighPercentileAdu = 9584, TargetToSkyContrast = 19, ContinuumSnrPerResolutionElement = 127.48, LineSnrPerResolutionElement = 194.93 };
        Assert.True((await f.Science.SelectExposureAsync(f.Context, default)).CanAdvance);
        Assert.Equal(300, f.Probe.SelectedExposureSeconds);
        Assert.Equal(1, f.Science.AcceptedFrames);
        Assert.True((await f.Science.RunAsync(f.Context, default)).CanAdvance);
        Assert.Equal(new double[] { 30, 300, 300 }, f.Port.Captures.Select(c => c.Metrics.ExposureSeconds));
        Assert.Equal(2, f.Science.AcceptedFrames);
        Assert.Equal(600, f.Science.AcceptedExposureSeconds);
    }

    [Fact]
    public async Task RejectedGuideFrameIsRetainedAndRecoveryCannotReissueScienceBudget()
    {
        var f = new AtrWorkflowFixture(3, attempts: 2);
        await f.Science.SelectExposureAsync(f.Context, default);
        f.Port.NextMetrics = m => m with { GuidingStable = false };
        var result = await f.Science.RunAsync(f.Context, default);
        Assert.Equal("ATR_ATTEMPT_LIMIT", result.Gate.Code);
        Assert.Equal(1, f.Science.AcceptedFrames);
        Assert.Equal(2, f.Science.AttemptedFrames);
        Assert.Equal(2, f.Port.Saved.Count);
        Assert.True(f.Probe.RequiresReprobe);
        f.Probe.Invalidate();
        var events = f.Port.Events.Count;
        Assert.Equal("ATR_ATTEMPT_LIMIT", (await f.Science.RunAsync(f.Context, default)).Gate.Code);
        Assert.Equal(events, f.Port.Events.Count); // no reprobe, recovery, capture or save
    }

    [Theory]
    [InlineData("identity")] [InlineData("temperature")] [InlineData("cover")]
    [InlineData("stop")] [InlineData("immediate")]
    public async Task ProbeCannotBypassHardwareBoundaries(string boundary)
    {
        var f = new AtrWorkflowFixture();
        f.Port.Deny = boundary;
        if (boundary == "immediate")
            await Assert.ThrowsAsync<OperationCanceledException>(() => f.Science.SelectExposureAsync(f.Context, default));
        else
            Assert.False((await f.Science.SelectExposureAsync(f.Context, default)).CanAdvance);
        Assert.Empty(f.Port.Captures);
        Assert.Equal(0, f.Probe.AttemptedFrames);
        Assert.Equal(0, f.Science.AttemptedFrames);
    }

    [Theory]
    [InlineData("environment")] [InlineData("photometry")] [InlineData("cover")]
    [InlineData("stop")] [InlineData("immediate")]
    public async Task ScienceRechecksInterlocksBeforeEveryNewFrame(string boundary)
    {
        var f = new AtrWorkflowFixture();
        await f.Science.SelectExposureAsync(f.Context, default);
        f.Port.Deny = boundary;
        if (boundary == "immediate")
            await Assert.ThrowsAsync<OperationCanceledException>(() => f.Science.RunAsync(f.Context, default));
        else
            Assert.False((await f.Science.RunAsync(f.Context, default)).CanAdvance);
        Assert.Single(f.Port.Captures);
        Assert.Equal(1, f.Science.AttemptedFrames);
    }

    [Fact]
    public async Task SavedTemperatureFailureCannotCreditProbeOrScience()
    {
        var probe = new AtrWorkflowFixture();
        probe.Port.Deny = "saved-temperature";
        Assert.False((await probe.Science.SelectExposureAsync(probe.Context, default)).CanAdvance);
        Assert.Equal(1, probe.Probe.RetainedFrames);
        Assert.Equal(0, probe.Science.AcceptedFrames);
        var science = new AtrWorkflowFixture();
        await science.Science.SelectExposureAsync(science.Context, default);
        science.Port.Deny = "saved-temperature";
        Assert.False((await science.Science.RunAsync(science.Context, default)).CanAdvance);
        Assert.Equal(1, science.Science.AcceptedFrames);
        Assert.Equal(1, science.Science.RetainedFrames);
        Assert.True(science.Probe.RequiresReprobe);
    }

    [Fact]
    public async Task MeridianRequestPausesBeforeCaptureWithoutClearingSelectionOrCounters()
    {
        var f = new AtrWorkflowFixture();
        await f.Science.SelectExposureAsync(f.Context, default);
        f.Port.Meridian = true;
        Assert.Equal("MERIDIAN", (await f.Science.RunAsync(f.Context, default)).Gate.Code);
        Assert.Single(f.Port.Captures);
        Assert.Equal(30d, f.Probe.SelectedExposureSeconds);
        Assert.Equal(1, f.Science.AcceptedFrames);
    }
}

internal sealed class AtrWorkflowFixture
{
    internal readonly AtrTestPort Port = new();
    internal readonly ObservationContext Context = CreateContext();
    internal AtrProbeWorkflow Probe { get; }
    internal AtrScienceWorkflow Science { get; }
    internal AtrWorkflowFixture(int count = 3, IReadOnlyList<double>? tiers = null, int attempts = 6, double probeSeconds = 30)
    {
        var configuration = new AtrRunConfiguration(100, 256, 1, -10, 0, new(0, 0, 100, 100),
            DispersionAxis.Horizontal, 20, 60, tiers ?? [30], probeSeconds, count, attempts,
            .001, .001, .002, 3, 1.15, 8, 5);
        Probe = new(configuration, Port);
        Science = new(configuration, Probe, Port);
    }
    internal static ObservationContext CreateContext() => new(new("run", "night", new("star", "HD", 52, 46),
        new(30, 110, 100), DateTimeOffset.UtcNow, TimeSpan.FromMinutes(20), new(), new(), "atr", "g3", "qhy"));
}

internal sealed class AtrTestPort : IAtrObservationPort
{
    internal readonly List<string> Events = [];
    internal readonly List<AtrCapture> Captures = [];
    internal readonly List<AtrSavedImage> Saved = [];
    internal readonly List<string> ScienceReferences = [];
    internal Func<SpectralProbeMetrics, SpectralProbeMetrics> NextMetrics = m => m;
    internal string? Deny;
    internal bool Meridian;
    internal Func<int, GateResult>? SavedTemperature;
    internal Func<int, GateResult>? CoolingReadiness;
    private int coolingCalls;
    private GateResult Gate(string name) { Events.Add(name); return Deny == name ? GateResult.Unknown(name, name) : GateResult.Pass(name, name); }
    public GateResult ValidateAtrCameraIdentity(string id) => Gate("identity");
    public void ThrowIfGuideOutputFailed() => Events.Add("guide-output");
    public bool IsGuidingStable() => true;
    public Task<GateResult> WaitForAtrScienceTemperatureAsync(ObservationContext c, CancellationToken t)
    {
        t.ThrowIfCancellationRequested();
        var gate = Gate("temperature");
        return Task.FromResult(CoolingReadiness?.Invoke(++coolingCalls) ?? gate);
    }
    public Task<GateResult> EnsureOpticalCoverOpenAsync(ObservationContext c, CancellationToken t) => Task.FromResult(Gate("cover"));
    public GateResult? SequenceStopGate() => Deny == "stop" ? Gate("stop") : null;
    public bool MeridianRequested => Meridian;
    public bool RequestNativeMeridianBoundary(ObservationContext c, double exposure) => Meridian;
    public StageResult MeridianSegmentBoundary() => new(GateResult.Pass("MERIDIAN", "boundary"));
    public GateResult ValidateEnvironment(ObservationPlan plan) => Gate("environment");
    public Task<GateResult> CheckPhotometryHealthAsync(ObservationContext c, CancellationToken t) => Task.FromResult(Gate("photometry"));
    public bool PhotometryUnavailable => false;
    public bool PhotometryAvailable => true;
    public Task RequireImmediatePhysicalActionGatesAsync(ObservationContext c, CancellationToken t)
    {
        Events.Add("immediate"); t.ThrowIfCancellationRequested();
        if (Deny == "immediate") throw new OperationCanceledException();
        return Task.CompletedTask;
    }
    public Task<AtrCapture> CaptureAtrImageAsync(ObservationContext c, double exposure, string imageType, string title, CancellationToken t)
    {
        Events.Add("capture");
        var id = $"capture-{Captures.Count}";
        var role = imageType == NINA.Equipment.Model.CaptureSequence.ImageTypes.SNAPSHOT ? "PROBE" : "SCIENCE";
        var metrics = NextMetrics(new(exposure, 256, 42000, 65535, 0, 100, 100, 10, true, id));
        var capture = new AtrCapture(null!, metrics, id, role,
            new(c.Plan.Target.Name, c.Plan.ObservationRunId, role, id, c.Plan.NightSetupId, imageType, c.Plan.Target.Name), "atr");
        Captures.Add(capture); return Task.FromResult(capture);
    }
    public Task<AtrSavedImage> SaveAtrImageAsync(AtrCapture capture, int attempt, bool accepted, GateDisposition disposition, string code, string message, CancellationToken t)
    {
        Events.Add("save");
        var saved = new AtrSavedImage(SavedTemperature?.Invoke(Saved.Count) ?? Gate("saved-temperature"), capture.CaptureToken + ".fits", new() { ["fitsProvenanceVerified"] = "True" });
        Saved.Add(saved); return Task.FromResult(saved);
    }
    public Task<string> ComputeFileSha256Async(string path, CancellationToken t) => Task.FromResult(new string('A', 64));
    public void PublishScienceEvidence(string path, string sha, IReadOnlyDictionary<string, string> metadata) => ScienceReferences.Add(path);
    public void PublishAtrPreview(IImageData image, string caption, double center, double halfWidth) { }
    public void PublishFrameCounters() => Events.Add("counters");
    public void PublishAcquisitionProgress(ObservationContext c, string phase = "Ready") { }
    public Task WriteAuditBestEffortAsync(string kind, object payload) => Task.CompletedTask;
    public void Report(string message, double value = -1) { }
}
