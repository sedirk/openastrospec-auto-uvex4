using System.Globalization;
using UvexAdv.Nina.Plugin;
using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class ObservationAcquisitionProgressTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-15T16:00:00Z");
    private static ObservationAcquisitionProgress Progress() => new("run", 3, 1, 2, 6, 1, 600, 600,
        "Exposing", "SCIENCE", "frame", Now.AddSeconds(-120), 600);
    private static ObservationAcquisitionPresentation Build(ObservationAcquisitionProgress? p, ObservationRunState state = ObservationRunState.RunningAuto, string run = "run") =>
        ObservationAcquisitionPresentation.Build(p, run, state, Now, CultureInfo.GetCultureInfo("zh-CN"));

    [Fact] public void IntegrationAndFrameProgressAreDistinctAndIncludeCurrentFrame()
    {
        var view = Build(Progress());
        Assert.Equal(2, view.RemainingFrames); Assert.Equal(1080, view.RemainingExposureSeconds);
        Assert.Equal(100d / 3, view.BlockPercent); Assert.Equal(20, view.FramePercent);
        Assert.Contains("第 2/3", view.Summary); Assert.Contains("复用试拍 1", view.Summary);
        Assert.Contains("00:08:00", view.Timing); Assert.Contains("00:10:00", view.Timing);
    }
    [Fact] public void SavingDoesNotAcceptAFrameAndDoesNotCountItsShutterTimeAgain()
    {
        var view = Build(Progress() with { Phase = "Processing" });
        Assert.Equal(600, view.RemainingExposureSeconds); Assert.Equal(2, view.RemainingFrames);
        Assert.True(view.FrameIndeterminate); Assert.Contains("质量检查", view.Summary);
    }
    [Fact] public void ElapsedTimerIsNeverProofOfAcceptance()
    {
        var view = Build(Progress() with { CaptureStartedUtc = Now.AddHours(-1) });
        Assert.Equal(99, view.FramePercent); Assert.True(view.FrameIndeterminate);
        Assert.Equal(2, view.RemainingFrames); Assert.Equal(600, view.RemainingExposureSeconds);
    }
    [Theory]
    [InlineData(ObservationRunState.PausedNeedsAttention)]
    [InlineData(ObservationRunState.Paused)]
    [InlineData(ObservationRunState.Cancelled)]
    public void PauseOrTerminalNeverKeepsCountingDown(ObservationRunState state)
    {
        var view = Build(Progress(), state);
        Assert.Null(view.RemainingExposureSeconds); Assert.Equal(0, view.FramePercent);
        Assert.False(view.FrameIndeterminate); Assert.Equal(2, view.RemainingFrames);
    }
    [Fact] public void UnknownProbeTierHasNoInventedScienceEta()
    {
        var view = Build(Progress() with { Role = "PROBE", SelectedExposureSeconds = null });
        Assert.Null(view.RemainingExposureSeconds); Assert.Contains("试拍选档", view.Summary);
    }
    [Fact] public void WrongRunAndNoProgressAreHidden()
    {
        Assert.False(Build(Progress(), run: "new-run").Visible);
        Assert.False(Build(null).Visible);
        Assert.False(Build(Progress() with { RequestedFrames = 0 }).Visible);
    }
    [Fact] public void CompletedAndEnglishRemainReadable()
    {
        var p = Progress() with { Phase = "Complete", AcceptedFrames = 3, AcceptedExposureSeconds = 1800 };
        var view = ObservationAcquisitionPresentation.Build(p, "run", ObservationRunState.Completed, Now, CultureInfo.GetCultureInfo("en-US"));
        Assert.Equal(100, view.BlockPercent); Assert.Equal(0, view.RemainingFrames);
        Assert.Contains("Accepted 3/3", view.Summary); Assert.Contains("00:30:00", view.Timing);
    }
}
