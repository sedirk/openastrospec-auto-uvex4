using System.Text.Json;
using NINA.Astrometry.Interfaces;
using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem.Utility;
using UvexAdv.Nina.Plugin.SequenceItems;
using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed partial class NativeSequencePlanTests
{
    [Theory]
    [InlineData("leaf", ObservationStage.AcquireG3SlitField)]
    [InlineData("leaf", ObservationStage.PlaceTargetOnSlit)]
    [InlineData("leaf", ObservationStage.RunScienceBlock)]
    [InlineData("target", ObservationStage.AcquireG3SlitField)]
    [InlineData("native-current-command", ObservationStage.RunScienceBlock)]
    [InlineData("native-set-command", ObservationStage.AcquireG3SlitField)]
    [InlineData("target-skip", ObservationStage.PlaceTargetOnSlit)]
    [InlineData("target-double", ObservationStage.RunScienceBlock)]
    [InlineData("condition", ObservationStage.AcquireG3SlitField)]
    [InlineData("to-end", ObservationStage.PlaceTargetOnSlit)]
    [InlineData("to-end-last", ObservationStage.RunScienceBlock)]
    [InlineData("to-end-cleanup-failed", ObservationStage.RunScienceBlock)]
    [InlineData("to-end-parent-cancel", ObservationStage.RunScienceBlock)]
    [InlineData("to-end-unsafe", ObservationStage.PlaceTargetOnSlit)]
    [InlineData("to-end-keep-open", ObservationStage.RunScienceBlock)]
    [InlineData("root-cancel", ObservationStage.RunScienceBlock)]
    [InlineData("leaf-stop-failed", ObservationStage.AcquireG3SlitField)]
    [InlineData("leaf-unsafe", ObservationStage.RunScienceBlock)]
    [InlineData("leaf-parent-cancel", ObservationStage.AcquireG3SlitField)]
    [InlineData("to-end-stop-failed", ObservationStage.RunScienceBlock)]
    public async Task NativeRootSkipScopesExecuteCorrectNextTargetOrEnd(string mode, ObservationStage stopStage)
    {
        InitializeAstrometry();
        var settings = Settings();
        var profile = Proxy<IProfile>((m, _) => m.Name == "get_AstrometrySettings" ? Proxy<IAstrometrySettings>() : Default(m.ReturnType));
        var profiles = Proxy<IProfileService>((m, _) => m.Name == "get_ActiveProfile" ? profile : Default(m.ReturnType));
        using var host = new ObservationCoordinatorHost(new SilentNotifier());
        var root = new SequenceRootContainer();
        root.Add(new StartAreaContainer());
        var area = new TargetAreaContainer(); root.Add(area);
        var night = new UvexNightSequenceContainer(profiles, host, null!, settings)
        { StopAtDawn = false, EndAtIso8601 = DateTimeOffset.UtcNow.AddHours(1).ToString("O") };
        area.Add(night);
        var first = new SkipProbeRunner(stopStage, mode.Contains("stop-failed"), mode.Contains("cleanup-failed"));
        var second = new SkipProbeRunner(null, false);
        UvexTargetObservationContainer Target(string name, SkipProbeRunner runner) => new(
            profiles, Proxy<INighttimeCalculator>(), host, null!, settings, () => runner)
        {
            TargetName = name, CatalogId = "SIM-" + name, NightSetupId = "SIM-NIGHT", UseRealMode = false,
            SiteLatitudeDegrees = 33, SiteLongitudeDegreesEast = 120, RightAscensionDegrees = 0, DeclinationDegrees = 89,
            DurationMinutes = 1, HorizonMinimumDegrees = 0, HorizonStartMarginDegrees = 0, HorizonContinueMarginDegrees = 0,
            ExpectedAtrCameraId = "SIM-ATR", ExpectedG3ProfileName = "SIM-G3", ExpectedQhyCameraId = "SIM-QHY",
            ScienceFrames = 2, MaximumScienceAttempts = 4,
        };
        var target = Target("Interrupted", first); night.Add(target);
        var targetCondition = new EndTargetCondition();
        if (mode == "condition") { target.Conditions.Add(targetCondition); targetCondition.AttachNewParent(target); }
        if (mode != "to-end-last") night.Add(Target("Next", second));
        var end = new EndAreaContainer(); root.Add(end);
        end.Add(new UvexObservatoryCloseoutItem { CloseAfterTargets = mode != "to-end-keep-open" });
        var group = new SequentialContainer(); end.Add(group);
        var condition = new UvexNightClosedCondition(); group.Conditions.Add(condition); condition.AttachNewParent(group);
        var endProbe = new Annotation { Text = "No hardware; must run only after verified closure." }; group.Add(endProbe);
        var run = root.Run(new Progress<ApplicationStatus>(), default);
        await first.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(target.CanEditPlan);
        Task interruption;
        if (mode.StartsWith("to-end")) interruption = InvokeNativeSequenceCommand(root, "SkipToEndOfSequence");
        else if (mode == "root-cancel") interruption = root.Interrupt();
        else if (mode == "target") interruption = target.Interrupt();
        else if (mode == "target-skip") { target.Skip(); interruption = Task.CompletedTask; }
        else if (mode == "target-double") interruption = Task.WhenAll(target.Interrupt(), target.Interrupt());
        else if (mode == "condition")
        {
            targetCondition.Continue = false; first.ReleaseStage.TrySetResult(); interruption = Task.CompletedTask;
        }
        else if (mode == "native-current-command") interruption = InvokeNativeSequenceCommand(root, "SkipCurrentItem");
        else if (mode == "native-set-command")
        {
            var marker = target.Items.OfType<ObservationStageMarkerItem>().Single(x => x.Stage == stopStage);
            marker.ErrorBehavior = NINA.Sequencer.Utility.InstructionErrorBehavior.SkipInstructionSetOnError;
            typeof(NINA.Sequencer.SequenceItem.SequenceItem).GetMethod("RunErrorBehavior",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(marker, [root]);
            interruption = Task.CompletedTask;
        }
        else { root.SkipCurrentRunningItems(); interruption = Task.CompletedTask; }
        if (mode.EndsWith("-unsafe")) night.OnNightUnsafe();
        if (mode.EndsWith("-parent-cancel")) _ = root.Interrupt();
        // Cancellation is joined, not merely requested, before another target or
        // any End instruction may execute. Exercise a deliberately delayed owner.
        await first.CleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(second.Stages);
        Assert.NotEqual(SequenceEntityStatus.FINISHED, endProbe.Status);
        first.ReleaseCleanup.TrySetResult();
        try { await interruption.WaitAsync(TimeSpan.FromSeconds(8)); }
        catch (OperationCanceledException) when (mode.EndsWith("-parent-cancel")) { }
        await run.WaitAsync(TimeSpan.FromSeconds(8));
        var next = mode is "leaf" or "target" or "native-current-command" or "native-set-command" or "target-skip" or "target-double" or "condition";
        var endRuns = next || mode is "to-end" or "to-end-last";
        Assert.Equal(next, second.Stages.Contains(ObservationStage.FinalizeObservation));
        Assert.Equal(endRuns, endProbe.Status == SequenceEntityStatus.FINISHED);
        Assert.Equal(endRuns, night.NativeEndInstructionsReady);
        Assert.True(target.CanEditPlan);
        Assert.DoesNotContain(ObservationStage.FinalizeObservation, first.Stages);
        using var journal = JsonDocument.Parse(await File.ReadAllTextAsync(night.JournalPath));
        var outcome = journal.RootElement.GetProperty("outcomes").EnumerateArray().First(x => x.TryGetProperty("outcome", out _));
        Assert.Equal(next ? mode == "condition" ? "SkippedNativeCondition" : "SkippedByOperator" : "Stopped", outcome.GetProperty("outcome").GetString());
        if (stopStage == ObservationStage.RunScienceBlock)
        {
            var retained = outcome.GetProperty("retainedScience");
            Assert.Equal(1, retained.GetProperty("acceptedFrames").GetInt32());
            Assert.Equal(2, retained.GetProperty("attemptedFrames").GetInt32());
            Assert.Equal(600, retained.GetProperty("acceptedExposureSeconds").GetDouble());
        }
        if (next) Assert.Contains(mode == "condition" ? "原生条件" : "操作员", target.LastOutcome);
        if (mode == "to-end") Assert.Equal("NATIVE_END_AREA", host.Dashboard.SafetyStandby?.Reason);
        if (endRuns)
        {
            // Re-running only End or clicking an old target must not replay
            // optional device commands or cancel a newer/completed target.
            await target.Interrupt();
            end.ResetAll();
            await end.Run(new Progress<ApplicationStatus>(), default);
            Assert.NotEqual(SequenceEntityStatus.FINISHED, endProbe.Status);
        }
        if (host.SafetyStandby is { } standby)
        {
            // Hardware-free cleanup of this test's guardian, not a new observation.
            standby.StopForApplicationExit();
            await standby.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    private static Task<bool> InvokeNativeSequenceCommand(SequenceRootContainer root, string method)
    {
        // Invoke the installed N.I.N.A. command implementation, not a copied
        // interpretation of its cancellation scope. Bypass only VM construction:
        // no N.I.N.A. Application, background validator or hardware mediators.
        var type = System.Reflection.Assembly.Load("NINA").GetType("NINA.ViewModel.Sequencer.Sequence2VM", true)!;
        var vm = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(type);
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        type.GetField("sequencer", flags)!.SetValue(vm,
            Proxy<NINA.Sequencer.ISequencer>((m, _) => m.Name == "get_MainContainer" ? root : Default(m.ReturnType)));
        return (Task<bool>)type.GetMethod(method, flags)!.Invoke(vm, [null])!;
    }

    private sealed class EndTargetCondition : NINA.Sequencer.Conditions.SequenceCondition
    {
        public volatile bool Continue = true;
        public override bool Check(NINA.Sequencer.SequenceItem.ISequenceItem previousItem,
            NINA.Sequencer.SequenceItem.ISequenceItem nextItem) => Continue && Parent.Iterations < 1;
        public override object Clone() => new EndTargetCondition();
    }

    private sealed class SkipProbeRunner(ObservationStage? waitAt, bool failStop, bool failCleanup = false) : ObservationStageRunnerBase, INativeTargetBoundary
    {
        private NativeTargetSession? session;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseStage { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CleanupEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseCleanup { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal List<ObservationStage> Stages { get; } = [];
        public void AttachNativeTarget(NativeTargetSession? value) => session = value;
        public void RecordMeridianSegment(NativeTargetSession session, string runId) { }
        public ClockContinuationBudget CaptureClockContinuationBudget() => throw new InvalidOperationException("Skip must not start recovery.");
        public Task<GateResult> RecoverMountClockAtIdleBoundaryAsync(ObservationPlan failedPlan, CancellationToken token) =>
            throw new InvalidOperationException("Skip must not start recovery.");
        public override async Task<StageResult> ExecuteStageAsync(ObservationStage stage, ObservationContext context, CancellationToken token)
        {
            Stages.Add(stage);
            if (stage == waitAt)
            {
                if (stage == ObservationStage.RunScienceBlock)
                    session!.RecordSegment(context.Plan.ObservationRunId, 1, 2, 0, 600);
                Entered.TrySetResult(); await ReleaseStage.Task.WaitAsync(token);
            }
            return new(GateResult.Pass("SIM_OK", "No hardware."));
        }
        public override async Task OnCancelledAsync(ObservationContext context, CancellationToken token)
        {
            CleanupEntered.TrySetResult();
            await ReleaseCleanup.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (failStop || failCleanup) throw new InvalidOperationException("Synthetic stop was not confirmed.");
        }
        public Task<IReadOnlyList<string>> ConfirmSequenceIdleAsync(CancellationToken token, bool requireSettledMotion = true) =>
            Task.FromResult<IReadOnlyList<string>>(failStop ? ["STOP_NOT_CONFIRMED"] : []);
    }
}
