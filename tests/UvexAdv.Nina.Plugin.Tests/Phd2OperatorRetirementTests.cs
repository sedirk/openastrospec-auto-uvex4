using System.Globalization;
using UvexAdv.Nina.Plugin;
using UvexAdv.Observatory;
using UvexAdv.Phd2;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class Phd2OperatorRetirementTests
{
    private static Phd2LockShiftPendingState State() => Phd2SlitPlacementCommissioningTests.CreateState("old-run", Phd2LockShiftPendingPhase.ReturnRequired);
    private static Phd2OperatorRetirementWitness Witness() => new(true, true, true, true, true,
        "mount", Phd2AppState.Stopped, true, DateTimeOffset.UtcNow);

    [Theory]
    [InlineData("consent")][InlineData("automation")][InlineData("camera")]
    [InlineData("mountDisconnected")][InlineData("motion")][InlineData("mountIdentity")]
    [InlineData("guiding")][InlineData("identity")][InlineData("stale")][InlineData("future")]
    public void BusyUnknownOrUnconfirmedTakeoverIsRejected(string fault)
    {
        var state = State(); var witness = Witness();
        witness = fault switch
        {
            "consent" => witness with { OperatorConfirmed = false },
            "automation" => witness with { AutomationIdle = false },
            "camera" => witness with { CameraIdle = false },
            "mountDisconnected" => witness with { MountConnected = false },
            "motion" => witness with { MountStationary = false },
            "mountIdentity" => witness with { TelescopeId = "other" },
            "guiding" => witness with { Phd2State = Phd2AppState.Guiding },
            "identity" => witness with { Phd2IdentityMatches = false },
            "stale" => witness with { CapturedUtc = DateTimeOffset.UtcNow.AddMinutes(-1) },
            _ => witness with { CapturedUtc = DateTimeOffset.UtcNow.AddMinutes(1) },
        };
        Assert.Throws<InvalidOperationException>(() => Phd2OperatorRetirement.Validate(state,
            ObservationRunState.Cancelled, state.ObservationRunId, "mount", witness, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void ExplicitIdleTakeoverDoesNotNeedAHomeButCancellationAloneIsInsufficient()
    {
        var state = State(); var witness = Witness();
        Phd2OperatorRetirement.Validate(state, ObservationRunState.Cancelled, state.ObservationRunId, "mount", witness, DateTimeOffset.UtcNow);
        foreach (var terminal in new ObservationRunState?[] { null, ObservationRunState.RunningAuto, ObservationRunState.Completed })
            Assert.Throws<InvalidOperationException>(() => Phd2OperatorRetirement.Validate(state, terminal,
                state.ObservationRunId, "mount", witness, DateTimeOffset.UtcNow));
        Assert.Throws<InvalidOperationException>(() => Phd2OperatorRetirement.Validate(state,
            ObservationRunState.Cancelled, "other-run", "mount", witness, DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData("valid")][InlineData("duplicate")][InlineData("active")][InlineData("changed")][InlineData("hash")]
    public async Task DurableTakeoverRetainsBudgetAndEndpointsAndNeverClaimsReturn(string condition)
    {
        var root = Path.Combine(Path.GetTempPath(), "uvex-retirement-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var state = State();
            var path = Path.Combine(root, state.ObservationRunId, "control", "phd2-lock-shift-pending.json");
            var journal = new ObservationRunJournalStore(Path.Combine(root, state.ObservationRunId, "manifest.json"));
            var plan = new ObservationPlan(state.ObservationRunId, "night", new("target", "catalog", 89, 46),
                new(31, 121, 20), DateTimeOffset.UtcNow, TimeSpan.FromHours(2), new(), new(), "ATR", "G3", "QHY");
            var manifest = await journal.InitializeAsync(plan, new(CommissioningPresetSha256: state.CommissioningPresetSha256,
                AdditionalHashes: new Dictionary<string,string> { ["actionConfigurationSha256"] = state.ActionConfigurationSha256 },
                Labels: new Dictionary<string,string> { ["telescopeId"] = "mount" }));
            await journal.PublishSnapshotAsync(manifest.Snapshot with { State = condition == "active" ? ObservationRunState.RunningAuto : ObservationRunState.Cancelled, UpdatedUtc = DateTimeOffset.UtcNow },
                manifest.Counters, manifest.Revision);
            await Phd2LockShiftPendingStore.WriteAtomicAsync(path, state, CancellationToken.None);
            var beforeManifest = await File.ReadAllBytesAsync(journal.ManifestPath);
            if (condition != "valid")
            {
                if (condition == "duplicate")
                    await Phd2LockShiftPendingStore.WriteAtomicAsync(Path.Combine(root, "duplicate", "control", "phd2-lock-shift-pending.json"), state, CancellationToken.None);
                if (condition == "changed")
                    await Phd2LockShiftPendingStore.WriteAtomicAsync(path, state with { LastReason = "changed" }, CancellationToken.None);
                if (condition == "hash")
                    await File.AppendAllTextAsync(path, "corrupt");
                var before = await File.ReadAllBytesAsync(path);
                await Assert.ThrowsAsync<InvalidOperationException>(() => Phd2OperatorRetirement.RetireAsync(root,
                    new(path, state, null), Witness(), CancellationToken.None));
                Assert.Equal(before, await File.ReadAllBytesAsync(path));
                Assert.Equal(beforeManifest, await File.ReadAllBytesAsync(journal.ManifestPath));
                Assert.Empty(Directory.GetFiles(root, "phd2-operator-takeover-*.json", SearchOption.AllDirectories));
                return;
            }
            var audit = await Phd2OperatorRetirement.RetireAsync(root, new(path, state, null), Witness(), CancellationToken.None);
            var retired = (await Phd2LockShiftPendingStore.LoadAsync(path, CancellationToken.None)).State!;
            Assert.Equal(Phd2LockShiftPendingPhase.RetiredCancelledObservation, retired.Phase);
            Assert.Equal(state, retired with { Phase = state.Phase, UpdatedUtc = state.UpdatedUtc, LastReason = state.LastReason });
            Assert.Equal(beforeManifest, await File.ReadAllBytesAsync(journal.ManifestPath));
            Assert.Contains("\"returnVerified\": false", await File.ReadAllTextAsync(audit));
            Assert.Empty(await Phd2LockShiftPendingStore.DiscoverAsync(root, CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() => Phd2OperatorRetirement.RetireAsync(root,
                new(path, state, null), Witness(), CancellationToken.None));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("zh-CN")][InlineData("en-US")]
    public void CalibrationRecoveryExplainsOldDebtAndShowsActualRateMismatch(string language)
    {
        var calibration = new Phd2CalibrationValidation(new(2, "guide"),
            new(true, 82.1, 17.337, "+", 173.4, 27.357, "+", 45.3), DateTimeOffset.UtcNow,
            TimeSpan.FromDays(31.21), 1.3, ["Guide rate changed; calibration too old."], [])
        {
            NativeDetails = new(DateTimeOffset.UtcNow.AddDays(-31), 7.520256, 7.520256),
            CurrentRaGuideRateArcsecondsPerSecond = 15.0405, CurrentDecGuideRateArcsecondsPerSecond = 15.0405,
        };
        var gate = Phd2RecoveryCalibrationPresentation.Blocked(calibration, "old-run", State());
        Assert.Equal(7.520256, gate.Metrics!["calibrationRaGuideRateArcsecondsPerSecond"]);
        Assert.Equal(15.0405, gate.Metrics["currentRaGuideRateArcsecondsPerSecond"]);
        Assert.Equal(0, gate.Metrics["guideOrRecalibrationCommandSent"]);
        Assert.Contains("old-run", gate.Message);
        var p = ObservationUiPresentation.Present(ObservationStage.PlaceTargetOnSlit, gate, CultureInfo.GetCultureInfo(language));
        if (language == "zh-CN")
        {
            Assert.Contains("旧运行", p.Summary);
            Assert.Contains("人工接管", p.Recommendation);
            Assert.Contains("只作警告", p.Recommendation);
            Assert.Contains("插件不强制重校准", p.Recommendation);
            Assert.DoesNotContain("最多执行一次", p.Recommendation);
            Assert.DoesNotContain("验证/刷新校准", p.Recommendation);
        }
        else
        {
            Assert.False(ObservationUiPresentation.ContainsCjk(p.Summary + p.Recommendation));
            Assert.Contains("PHD2 owns calibration", p.Recommendation);
            Assert.DoesNotContain("validate/refresh calibration", p.Recommendation);
        }
        Assert.False(ObservationAutomaticRecoveryPolicy.For(ObservationStage.PlaceTargetOnSlit, gate).IsRecoverable);
    }

    [Fact]
    public void OnlyVisibleClearActionMayOfferTakeoverAndNeverCommandsHardware()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "ObservationDockable.cs"));
        var start = source.IndexOf("private async Task ClearG3RecoveryStateAsync()", StringComparison.Ordinal);
        var end = source.IndexOf("private async Task RestartWithCurrentConfigurationAsync()", start, StringComparison.Ordinal);
        var body = source[start..end];
        Assert.Contains("MessageBoxResult.No", body);
        Assert.Contains("Phd2OperatorRetirement.RetireAsync", body);
        foreach (var forbidden in new[] { "StartGuiding", "StopCapture", "SetConnected", "MoveAxis", "SetLockPosition", "StartRealAsync" })
            Assert.DoesNotContain(forbidden, body);
        var restartEnd = source.IndexOf("private async Task<bool> RetireG3RecoveryStateAsync()", end, StringComparison.Ordinal);
        Assert.DoesNotContain("Phd2OperatorRetirement", source[end..restartEnd]);
    }
}
