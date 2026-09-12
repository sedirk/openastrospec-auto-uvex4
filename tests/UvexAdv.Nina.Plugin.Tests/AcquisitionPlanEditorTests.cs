using System.Globalization;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class AcquisitionPlanEditorTests
{
    private static AcquisitionPlanValues Initial => new(3, 6, "0.1,3,120,600", 0.1, 60);
    private static AcquisitionPlanEditor Editor(Action<AcquisitionPlanValues>? save = null, bool editable = true, string language = "zh-CN") =>
        new(() => Initial, save ?? (_ => { }), () => editable, () => CultureInfo.GetCultureInfo(language));

    [Fact]
    public void EditsAreDraftsUntilValidatedExplicitSaveAndReloadAcrossInstances()
    {
        var stored = Initial;
        var calls = 0;
        var editor = new AcquisitionPlanEditor(() => stored, value => { calls++; stored = value; }, () => true, () => CultureInfo.GetCultureInfo("zh-CN"));
        editor.ScienceFrames = "12";
        editor.MaximumAttempts = "18";
        editor.ExposureLadderSeconds = "600，0.1,3,3";
        Assert.Equal(3, stored.ScienceFrames);
        Assert.True(editor.HasPendingChanges);
        Assert.True(editor.SaveCommand.CanExecute(null));
        editor.SaveCommand.Execute(null);
        Assert.Equal(1, calls);
        Assert.Equal(12, stored.ScienceFrames);
        Assert.Equal(18, stored.MaximumAttempts);
        Assert.Equal("0.1,3,600", stored.ExposureLadderSeconds);
        Assert.False(editor.HasPendingChanges);
        var restarted = new AcquisitionPlanEditor(() => stored, _ => { }, () => true, () => CultureInfo.GetCultureInfo("zh-CN"));
        Assert.Equal("12", restarted.ScienceFrames);
        Assert.Equal(editor.BudgetSummary, restarted.BudgetSummary);
    }

    [Theory]
    [InlineData("frames", "0")]
    [InlineData("frames", "-1")]
    [InlineData("frames", "1.5")]
    [InlineData("frames", "abc")]
    [InlineData("attempts", "2")]
    [InlineData("attempts", "NaN")]
    [InlineData("ladder", "")]
    [InlineData("ladder", "0.1,,3")]
    [InlineData("ladder", "0.1,3,")]
    [InlineData("ladder", "0.1,0")]
    [InlineData("ladder", "0.1,-3")]
    [InlineData("ladder", "0.1,Infinity")]
    [InlineData("ladder", "0.1,NaN")]
    [InlineData("ladder", "0.1,1e308")]
    [InlineData("probe", "2")]
    [InlineData("probe", "-0.1")]
    [InlineData("probe", "NaN")]
    [InlineData("minutes", "0")]
    [InlineData("minutes", "Infinity")]
    [InlineData("minutes", "1e300")]
    public void InvalidDraftCannotPersistOrSilentlyFallBack(string field, string text)
    {
        var calls = 0;
        var editor = Editor(_ => calls++);
        switch (field)
        {
            case "frames": editor.ScienceFrames = text; break;
            case "attempts": editor.MaximumAttempts = text; break;
            case "ladder": editor.ExposureLadderSeconds = text; break;
            case "probe": editor.ProbeSeconds = text; break;
            case "minutes": editor.PlanningMinutes = text; break;
        }
        Assert.True(editor.HasPendingChanges);
        Assert.True(editor.HasValidationError);
        Assert.False(editor.SaveCommand.CanExecute(null));
        editor.SaveCommand.Execute(null);
        Assert.Equal(0, calls);
        Assert.DoesNotContain("～", editor.BudgetSummary);
    }

    [Fact]
    public void ActiveRunLocksFieldsSaveAndRestore()
    {
        var editor = Editor(_ => throw new InvalidOperationException("must not save"), editable: false);
        editor.ScienceFrames = "30";
        Assert.Equal("3", editor.ScienceFrames);
        Assert.False(editor.IsEditable);
        Assert.False(editor.SaveCommand.CanExecute(null));
        Assert.False(editor.ReloadCommand.CanExecute(null));
        Assert.Contains("运行已锁定", editor.StateText);
    }

    [Fact]
    public void BecomingActiveAfterDraftDisablesPendingSave()
    {
        var idle = true;
        var calls = 0;
        var editor = new AcquisitionPlanEditor(() => Initial, _ => calls++, () => idle, () => CultureInfo.GetCultureInfo("en-US"));
        editor.ScienceFrames = "4";
        idle = false;
        editor.NotifyState();
        editor.SaveCommand.Execute(null);
        Assert.Equal(0, calls);
        Assert.True(editor.HasPendingChanges);
    }

    [Fact]
    public void SaveFailureDoesNotReportAppliedAndRestoreDiscardsDraft()
    {
        var editor = Editor(_ => throw new IOException("disk error"));
        editor.ScienceFrames = "4";
        editor.SaveCommand.Execute(null);
        Assert.Contains("保存失败", editor.StateText);
        Assert.Contains("3 张", editor.SavedSummary);
        Assert.True(editor.HasPendingChanges);
        editor.ReloadCommand.Execute(null);
        Assert.Equal("3", editor.ScienceFrames);
        Assert.False(editor.HasPendingChanges);
    }

    [Fact]
    public void BudgetUsesAcceptedAndAttemptCountsSeparatelyAndDoesNotClaimWallClockCutoff()
    {
        var editor = Editor();
        Assert.Contains("30 分钟", editor.BudgetSummary);
        Assert.Contains("1 小时", editor.BudgetSummary);
        Assert.Contains("不含试拍", editor.BudgetSummary);
        Assert.Contains("不是曝光硬截止", editor.PlanningWarning);
        editor.PlanningMinutes = "120";
        Assert.Empty(editor.PlanningWarning);
    }

    [Fact]
    public void EnglishDraftAndValidationDoNotLeakChineseSentences()
    {
        var editor = Editor(language: "en-US");
        Assert.Contains("Accepted science integration", editor.BudgetSummary);
        Assert.DoesNotMatch("[\\u4e00-\\u9fff]", editor.BudgetSummary + editor.StateText + editor.SavedSummary + editor.PlanningWarning);
        editor.ScienceFrames = "NaN";
        Assert.Contains("positive integer", editor.ValidationMessage);
    }

    [Fact]
    public void ProductionEntryRemainsSharedAndSaveRollsBackSettingsOnPersistenceFailure()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "ObservationDockable.cs"));
        var start = source.IndexOf("private void SaveAcquisitionPlan", StringComparison.Ordinal);
        var end = source.IndexOf("public string NightSetupId", start, StringComparison.Ordinal);
        var save = source[start..end];
        Assert.Contains("IsTargetPlanEditable", save);
        Assert.Contains("plan.Apply(settings)", save);
        Assert.Contains("ActiveProfile.Save()", save);
        Assert.Contains("previous.Apply(settings)", save);
        Assert.DoesNotContain("Mediator", save);
        Assert.Contains("AcquisitionPlan?.HasPendingChanges != true", source);
        var runner = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "RealObservationStageRunner.cs"));
        Assert.Contains("while (savedAtrFrames < configuration.Atr.ScienceFrameCount)", runner);
        Assert.Contains("configuration.Atr.MaximumScienceAttempts", runner);
    }
}
