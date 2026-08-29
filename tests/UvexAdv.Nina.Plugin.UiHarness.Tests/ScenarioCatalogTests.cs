using UvexAdv.Nina.Plugin;
using UvexAdv.Nina.Plugin.UiHarness;

namespace UvexAdv.Nina.Plugin.UiHarness.Tests;

public sealed class ScenarioCatalogTests
{
    [Fact]
    public void Catalog_CoversRequiredOperatorAndAdvancedSettingsStates()
    {
        var scenarios = ScenarioCatalog.Select(null);

        Assert.Equal(["idle", "uvex-manual", "startup-requirements", "running", "recovering", "atr-manual", "failure", "failure-en", "phd2-degraded", "phd2-direct-target", "ghost-assistance", "qhy-g3-fast-pair", "narrow", "advanced"], scenarios.Select(item => item.Name));
        Assert.Equal("zh-CN", scenarios.Single(item => item.Name == "failure").Culture.Name);
        Assert.Equal("en-US", scenarios.Single(item => item.Name == "failure-en").Culture.Name);
        Assert.True(scenarios.Single(item => item.Name == "narrow").Width <= 540);
        Assert.False(scenarios.Single(item => item.Name == "idle").ViewModel.HasFailure);
        Assert.True(scenarios.Single(item => item.Name == "running").ViewModel.IsRunActive);
        Assert.Equal(ObservationUiTone.Recovering, scenarios.Single(item => item.Name == "recovering").ViewModel.RunTone);
        var atrManual = scenarios.Single(item => item.Name == "atr-manual").ViewModel;
        Assert.Equal(4, atrManual.SelectedWorkspaceTabIndex);
        Assert.Equal(2, atrManual.SelectedPreviewTabIndex);
        Assert.NotEmpty(atrManual.ManualSpectrumPoints);
        Assert.True(atrManual.CaptureManualAtrSpectrumCommand.CanExecute(null));
        Assert.True(scenarios.Single(item => item.Name == "failure").ViewModel.HasFailure);
        Assert.Equal(0, scenarios.Single(item => item.Name == "idle").ViewModel.SelectedWorkspaceTabIndex);
        var startup = scenarios.Single(item => item.Name == "startup-requirements").ViewModel;
        Assert.Equal(3, startup.SelectedWorkspaceTabIndex);
        Assert.Contains("准备尚未完成", startup.RealModeStatusSummary, StringComparison.Ordinal);
        Assert.Contains("设备标定证据", startup.RealModeStatus, StringComparison.Ordinal);
        Assert.False(startup.IsDevicePreparationMissing);
        Assert.True(startup.IsCommissioningPreparationMissing);
        Assert.True(startup.IsNightSetupPreparationMissing);
        var manual = scenarios.Single(item => item.Name == "uvex-manual").ViewModel;
        Assert.Equal(1, manual.SelectedWorkspaceTabIndex);
        Assert.Contains("未连接", manual.ManualUvexConnectionStatus, StringComparison.Ordinal);
        Assert.Equal("UVEX4 / COM5", manual.SelectedManualUvexDevice);
        Assert.True(manual.ConnectManualUvexCommand.CanExecute(null));
        Assert.False(manual.DisconnectManualUvexCommand.CanExecute(null));
        Assert.False(manual.SelectManualSlit1Command.CanExecute(null));
        Assert.True(manual.ReleaseManualUvexComPortCommand.CanExecute(null));
        var advanced = scenarios.Single(item => item.Name == "advanced").ViewModel;
        Assert.Equal(6, advanced.SelectedWorkspaceTabIndex);
        Assert.True(advanced.BrightTargetWingCentroidEnabled);
        Assert.True(advanced.BrightTargetMinimumG3ExposureMilliseconds > 0);
        var degraded = scenarios.Single(item => item.Name == "phd2-degraded").ViewModel;
        Assert.Equal("DegradedSupervised", degraded.Phd2CalibrationGradeText);
        Assert.Contains("exact-lock：是", degraded.Phd2CalibrationPermissionText, StringComparison.Ordinal);
        Assert.Contains("无人值守科学：否", degraded.Phd2CalibrationPermissionText, StringComparison.Ordinal);
        Assert.Contains("0.5", degraded.Phd2CalibrationScaleText, StringComparison.Ordinal);
        Assert.Contains("0.75", degraded.Phd2CalibrationScaleText, StringComparison.Ordinal);
        Assert.Contains("OffSlitGuideStar", degraded.Phd2CommissioningRouteText, StringComparison.Ordinal);
        var directTarget = scenarios.Single(item => item.Name == "phd2-direct-target").ViewModel;
        Assert.Equal("Qualified", directTarget.Phd2CalibrationGradeText);
        Assert.Contains("DegradedDirectTargetGuiding", directTarget.Phd2CommissioningRouteText, StringComparison.Ordinal);
        Assert.Contains("10 ms", directTarget.Phd2CommissioningRouteText, StringComparison.Ordinal);
        Assert.Contains("无人值守科学：否", directTarget.Phd2CalibrationPermissionText, StringComparison.Ordinal);
        Assert.Contains("RequiresOperatorSupervision", directTarget.Phd2CalibrationReasonText, StringComparison.Ordinal);
        var ghost = scenarios.Single(item => item.Name == "ghost-assistance").ViewModel;
        Assert.Equal("AutoIfValidElseSkip", ghost.GhostAssistanceMode);
        Assert.Contains("GHOST_TEMPLATE_APPLICABLE", ghost.GhostApplicabilityText, StringComparison.Ordinal);
        Assert.Contains("UseCalibratedAuxiliaryEstimate", ghost.GhostDecisionText, StringComparison.Ordinal);
        Assert.Contains("不能建立身份或授权运动", ghost.GhostDecisionText, StringComparison.Ordinal);
        var fastPair = scenarios.Single(item => item.Name == "qhy-g3-fast-pair").ViewModel;
        Assert.True(fastPair.QhyG3FastPairEnabled);
        Assert.Equal(6, fastPair.SelectedWorkspaceTabIndex);
        Assert.Contains("0 次赤道仪命令", fastPair.QhyG3FastPairStatus, StringComparison.Ordinal);
        Assert.Contains("Candidate", fastPair.WideToSlitTransferStatus, StringComparison.Ordinal);
    }

    [Fact]
    public void MockStates_UseDeterministicImagesAndNoProductionDockable()
    {
        var running = ScenarioCatalog.Select("running").Single().ViewModel;
        var failure = ScenarioCatalog.Select("failure").Single().ViewModel;

        Assert.NotNull(running.QhyPreviewImage);
        Assert.NotNull(running.G3PreviewImage);
        Assert.NotNull(running.AtrPreviewImage);
        Assert.Contains("G3_FOCUS_STARS_TOO_BROAD", failure.LastFailureCode, StringComparison.Ordinal);
        Assert.Equal("UvexAdv.Nina.Plugin.UiHarness", running.GetType().Assembly.GetName().Name);
        Assert.NotEqual("UvexAdv.Nina.Plugin.ObservationDockable", running.GetType().FullName);
    }

    [Fact]
    public void TargetImport_IsVisibleWhenSuccessfulAndDisabledDuringRun()
    {
        var idle = ScenarioCatalog.Select("idle").Single().ViewModel;
        var running = ScenarioCatalog.Select("running").Single().ViewModel;
        var narrow = ScenarioCatalog.Select("narrow").Single().ViewModel;

        Assert.True(idle.HasTargetImport);
        Assert.True(idle.IsTargetPlanEditable);
        Assert.Contains("构图助手", idle.TargetImportSummary, StringComparison.Ordinal);
        Assert.False(idle.ImportFramingCenter);
        Assert.True(idle.ImportFromFramingAssistantCommand.CanExecute(null));
        Assert.True(idle.ImportFromPlanetariumCommand.CanExecute(null));
        Assert.False(running.ImportFromFramingAssistantCommand.CanExecute(null));
        Assert.False(narrow.ImportFromPlanetariumCommand.CanExecute(null));
        Assert.Contains("运行期间禁止", narrow.TargetImportDetails, StringComparison.Ordinal);
        Assert.False(narrow.IsTargetPlanEditable);
    }
}
