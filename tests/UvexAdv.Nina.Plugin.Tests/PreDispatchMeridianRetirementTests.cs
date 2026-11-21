using System.Security.Cryptography;
using System.Text.Json;
using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed partial class NativeSequencePlanTests
{
    [Fact]
    public void NewRetirementRemainsInSharedStartupBeforeTheStrictPendingFlipGate()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "UVEX-ADV.sln"))) directory = directory.Parent;
        Assert.NotNull(directory);
        var source = File.ReadAllText(Path.Combine(directory.FullName, "src", "UvexAdv.Nina.Plugin", "RealObservationStageRunner.cs"));
        Assert.True(source.IndexOf("var durableG3Recovery = await RecoverDurableG3AcquisitionBeforeStageAsync(", StringComparison.Ordinal) <
            source.IndexOf("ObservationStage.ValidateNightSetup => await ValidateNightSetupAsync", StringComparison.Ordinal));
        var setup = source.IndexOf("private async Task<StageResult> ValidateNightSetupAsync", StringComparison.Ordinal);
        Assert.Contains("NativeMeridianJournalStore.PendingGate(NativeMeridianJournalPath)", source.Substring(setup, 500));
        Assert.Contains("await TryRetireTerminalGuideStateAtStartupAsync(context, cancellationToken)", source);
        var startup = File.ReadAllText(Path.Combine(directory.FullName, "src", "UvexAdv.Nina.Plugin", "RealObservationStageRunner.StartupGuideRetirement.cs"));
        Assert.Contains("TerminalGuideStartupRetirement.TryRetireAsync", startup);
        Assert.Contains("LockedMetadata, Prepare, Capture, Witness, Check, boundaryToken", startup);
        Assert.Contains("startupGuideRetirementIncomplete = true", startup);
        Assert.Contains("qhy.ReadGlobalIdleAsync", startup);
        Assert.Contains("StartupGuideStationaryMount.ReadFreshAsync", startup);
        Assert.Contains("StartupGuideStationaryMount.Validate", startup);
        Assert.Contains("G3FieldMountBindingPolicy.ValidateProvenance", startup);
        Assert.DoesNotContain("ValidateForMotion", startup);
        Assert.DoesNotContain("SetTrackingEnabled", startup);
        Assert.DoesNotContain("MeridianFlipAsync", startup);
    }

    [Theory]
    [InlineData("success", "OwnersIdle")]
    [InlineData("success", "WaitingForMeridian")]
    [InlineData("fresh-quality-failure", "OwnersIdle")]
    [InlineData("untracked-drift", "OwnersIdle")]
    [InlineData("untracked-drift-quality", "OwnersIdle")]
    [InlineData("untracked-move", "OwnersIdle")]
    [InlineData("command-pending", "CommandPending")]
    [InlineData("unknown-phase", "FlipRequested")]
    [InlineData("missing-segment-gate", "OwnersIdle")]
    [InlineData("same-logical-target", "OwnersIdle")]
    [InlineData("active", "OwnersIdle")]
    [InlineData("paused", "OwnersIdle")]
    [InlineData("paused-attention", "OwnersIdle")]
    [InlineData("ambiguous-g3", "OwnersIdle")]
    [InlineData("ambiguous-phd", "OwnersIdle")]
    [InlineData("mechanical-debt", "OwnersIdle")]
    [InlineData("corrupt", "OwnersIdle")]
    [InlineData("original-binding-changed", "OwnersIdle")]
    [InlineData("original-context-changed", "OwnersIdle")]
    [InlineData("stop-unknown", "OwnersIdle")]
    [InlineData("camera-busy", "OwnersIdle")]
    [InlineData("mount-moving", "OwnersIdle")]
    [InlineData("owner-changed", "OwnersIdle")]
    [InlineData("remote-owner-configuration-changed", "OwnersIdle")]
    [InlineData("remote-camera-changed", "OwnersIdle")]
    [InlineData("stale-stop", "OwnersIdle")]
    [InlineData("capture-failed", "OwnersIdle")]
    [InlineData("unsafe-before", "OwnersIdle")]
    [InlineData("unsafe-after-frame", "OwnersIdle")]
    [InlineData("cancel-after-frame", "OwnersIdle")]
    [InlineData("deadline-after-frame", "OwnersIdle")]
    public async Task EndedUnissuedFlipUsesFreshNativeStartupAndReallyExecutesNextTarget(string mode, string phase)
    {
        await using var fixture = await TerminalStartupFixture.CreateAsync(mode, meridian: true);
        await fixture.AddPreDispatchMeridianAsync(mode, phase);
        var path = Path.Combine(fixture.Root, "control", "native-meridian-flip.json");
        var bytes = await File.ReadAllBytesAsync(path);
        await AssertTerminalStartupNightAsync(fixture, mode);
        if (mode is "success" or "fresh-quality-failure" or "untracked-drift" or "untracked-drift-quality") await AssertUnissuedFlipArchiveAsync(fixture.Root, bytes);
        else Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task EndedSecondSegmentUnissuedFlipUsesAuthenticatedClockContinuationLineageAndRunsNextTarget()
    {
        const string rootRun = "clock-continuation-root";
        await using var fixture = await TerminalStartupFixture.CreateAsync(
            "success", meridian: true, rootRunId: rootRun);
        await fixture.AddPreDispatchMeridianAsync("success");
        var path = Path.Combine(fixture.Root, "control", "native-meridian-flip.json");
        var bytes = await File.ReadAllBytesAsync(path);
        var journal = JsonSerializer.Deserialize<NativeMeridianJournal>(bytes)!;
        Assert.Equal(new[] { rootRun, fixture.RunId }, journal.Segments);

        await AssertTerminalStartupNightAsync(fixture, "success");

        await AssertUnissuedFlipArchiveAsync(fixture.Root, bytes);
        var archive = Assert.Single(Directory.GetFiles(fixture.Root,
            "native-meridian-flip.json.pre-dispatch-*.archive", SearchOption.AllDirectories));
        using var audit = JsonDocument.Parse(await File.ReadAllTextAsync(archive + ".audit.json"));
        Assert.Equal(rootRun, audit.RootElement.GetProperty("targetRootRunId").GetString());
        Assert.Equal(2, audit.RootElement.GetProperty("segments").GetArrayLength());
    }

    [Theory]
    [InlineData("changed-after-frame")]
    [InlineData("changed-manifest")]
    [InlineData("unsafe-after-guide-archive")]
    [InlineData("cancel-after-guide-archive")]
    [InlineData("duplicate")]
    [InlineData("corrupt-journal")]
    [InlineData("missing-manifest")]
    [InlineData("future")]
    [InlineData("invalid-segment-order")]
    [InlineData("forged-prior-segment")]
    [InlineData("after-side")]
    [InlineData("unknown-side")]
    [InlineData("phd-profile-change")]
    [InlineData("atr-change")]
    [InlineData("weak-supervision")]
    public async Task UnissuedFlipCannotHideLateChangesOrPartialRetirement(string failure)
    {
        await using var fixture = await TerminalStartupFixture.CreateAsync("success", meridian: true);
        await fixture.AddPreDispatchMeridianAsync("success");
        var path = Path.Combine(fixture.Root, "control", "native-meridian-flip.json");
        var entry = JsonSerializer.Deserialize<NativeMeridianJournal>(await File.ReadAllTextAsync(path))!;
        if (failure == "duplicate")
        {
            Directory.CreateDirectory(Path.Combine(fixture.Root, "unexpected"));
            File.Copy(path, Path.Combine(fixture.Root, "unexpected", "native-meridian-flip.json"));
        }
        if (failure == "corrupt-journal") await File.AppendAllTextAsync(path, "corrupt");
        if (failure == "missing-manifest") File.Move(fixture.ManifestPath, fixture.ManifestPath + ".missing");
        if (failure is "future" or "invalid-segment-order" or "forged-prior-segment" or "after-side" or "unknown-side")
            await NativeMeridianJournalStore.SaveAsync(path, entry with
            {
                UpdatedUtc = failure == "future" ? DateTimeOffset.UtcNow.AddMinutes(1) : entry.UpdatedUtc,
                Segments = failure == "invalid-segment-order" ? [entry.RunId, "second"] :
                    failure == "forged-prior-segment" ? ["different-root", entry.RunId] : entry.Segments,
                AfterSide = failure == "after-side" ? "pierEast" : "",
                BeforeSide = failure == "unknown-side" ? "pierUnknown" : entry.BeforeSide,
            }, default);
        var current = NewMeridianStartupMetadata(fixture.Manifest);
        if (failure == "phd-profile-change") current = current with { Phd2ProfileEvidenceSha256 = new string('F', 64) };
        if (failure is "atr-change" or "weak-supervision")
        {
            var labels = new Dictionary<string, string>(current.Labels!);
            labels[failure == "atr-change" ? "atrCameraId" : "weakSupervisionEnabled"] = failure == "atr-change" ? "different" : "True";
            current = current with { Labels = labels };
        }
        using var cancellation = new CancellationTokenSource();
        var error = await Record.ExceptionAsync(() => TerminalGuideStartupRetirement.TryRetireAsync(fixture.Root,
            "fresh-new-run", current, _ => Task.CompletedTask,
            async _ =>
            {
                if (failure == "changed-after-frame")
                    await NativeMeridianJournalStore.SaveAsync(path, entry with { Phase = "CommandPending" }, default);
                if (failure == "changed-manifest")
                    await new ObservationRunJournalStore(fixture.ManifestPath).PublishGateAsync(ObservationStage.AcquireG3SlitField,
                        GateResult.Pass("CHANGED", "Source changed while verifying."));
            }, _ => Task.FromResult(fixture.Witness()), ct =>
            {
                if (!File.Exists(fixture.LedgerPath))
                {
                    if (failure == "unsafe-after-guide-archive") throw new InvalidOperationException("SAFETY_UNSAFE");
                    if (failure == "cancel-after-guide-archive") cancellation.Cancel();
                }
                ct.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }, cancellation.Token));
        Assert.NotNull(error);
        Assert.True(File.Exists(path));
        Assert.Empty(Directory.GetFiles(fixture.Root, "native-meridian-flip.json.*.archive", SearchOption.AllDirectories));
        if (failure is "unsafe-after-guide-archive" or "cancel-after-guide-archive")
        {
            Assert.False(File.Exists(fixture.LedgerPath));
            var original = await File.ReadAllBytesAsync(path);
            var frames = 0;
            var result = await TerminalGuideStartupRetirement.TryRetireAsync(fixture.Root, "fresh-new-run", current,
                _ => Task.CompletedTask, _ => { frames++; return Task.CompletedTask; }, _ => Task.FromResult(fixture.Witness()),
                _ => Task.CompletedTask, default);
            Assert.Single(result!); // Only the unissued intent remains, but all fresh proof repeats.
            Assert.Equal(1, frames);
            await AssertUnissuedFlipArchiveAsync(fixture.Root, original);
        }
        else Assert.Equal(fixture.OriginalLedger, await File.ReadAllBytesAsync(fixture.LedgerPath));
    }

    [Fact]
    public async Task RecordedHd21455UnissuedHandoffRetiresInIsolatedCopyThenNextNativeTargetExecutes()
    {
        var observations = Environment.GetEnvironmentVariable("UVEX_REPLAY_OBSERVATIONS");
        if (string.IsNullOrWhiteSpace(observations)) return;
        const string run = "UVEX-20261029T163421Z-ab5c1d1496184ad";
        var source = Path.Combine(observations, run);
        var manifestPath = Path.Combine(source, "manifest.json");
        var ledgerPath = Path.Combine(source, "control", "g3-acquisition-motion.json");
        if (!File.Exists(ledgerPath)) ledgerPath = Assert.Single(Directory.GetFiles(Path.Combine(source, "control"),
            "g3-acquisition-motion.operator-cleared.*.json"), x => !x.EndsWith(".audit.json", StringComparison.Ordinal));
        var flipPath = Path.Combine(observations, "control", "native-meridian-flip.json");
        if (!File.Exists(flipPath) || JsonSerializer.Deserialize<NativeMeridianJournal>(File.ReadAllText(flipPath))?.RunId != run)
            flipPath = Assert.Single(Directory.GetFiles(Path.Combine(observations, "control"),
            "native-meridian-flip.json.pre-dispatch-*.archive"), x =>
                JsonSerializer.Deserialize<NativeMeridianJournal>(File.ReadAllText(x))?.RunId == run);
        var manifest = await File.ReadAllBytesAsync(manifestPath);
        var ledger = await File.ReadAllBytesAsync(ledgerPath);
        var flip = await File.ReadAllBytesAsync(flipPath);
        await using var fixture = await TerminalStartupFixture.CopyRecordedAsync(run, manifest, ledger);
        Directory.CreateDirectory(Path.Combine(fixture.Root, "control"));
        await File.WriteAllBytesAsync(Path.Combine(fixture.Root, "control", "native-meridian-flip.json"), flip);
        Assert.Equal("HD 21455", fixture.Manifest.Plan.Target.Name);
        await AssertTerminalStartupNightAsync(fixture, "success");
        await AssertUnissuedFlipArchiveAsync(fixture.Root, flip);
        Assert.Equal(SHA256.HashData(manifest), SHA256.HashData(await File.ReadAllBytesAsync(manifestPath)));
        Assert.Equal(SHA256.HashData(ledger), SHA256.HashData(await File.ReadAllBytesAsync(ledgerPath)));
        Assert.Equal(SHA256.HashData(flip), SHA256.HashData(await File.ReadAllBytesAsync(flipPath)));
    }

    [Theory]
    [InlineData("untracked-drift")]
    [InlineData("untracked-drift-quality")]
    public async Task RecordedOctober30FlipOnlyDebtPreservesHistoryAndExecutesNextNativeTarget(string mode)
    {
        var observations = Environment.GetEnvironmentVariable("UVEX_REPLAY_OBSERVATIONS");
        if (string.IsNullOrWhiteSpace(observations)) return;
        const string run = "UVEX-20261030T124839Z-d9b60e8ff3ef4be";
        var manifestPath = Path.Combine(observations, run, "manifest.json");
        var flipPath = Path.Combine(observations, "control", "native-meridian-flip.json");
        if (!File.Exists(flipPath) || JsonSerializer.Deserialize<NativeMeridianJournal>(File.ReadAllText(flipPath))?.RunId != run)
            flipPath = Assert.Single(Directory.GetFiles(Path.Combine(observations, "control"),
                "native-meridian-flip.json.pre-dispatch-*.archive"), x =>
                    JsonSerializer.Deserialize<NativeMeridianJournal>(File.ReadAllText(x))?.RunId == run);
        var manifest = await File.ReadAllBytesAsync(manifestPath);
        var flip = await File.ReadAllBytesAsync(flipPath);
        // The real state has ONLY the unissued flip handle: guide ledgers were
        // already archived by the meridian boundary. Do not recreate old debt.
        await using var fixture = await TerminalStartupFixture.CopyUnissuedFlipOnlyAsync(run, manifest, flip);
        Assert.False(fixture.HasGuideLedger);
        Assert.Equal("HD 222940", fixture.Manifest.Plan.Target.Name);
        Assert.Equal("OwnersIdle", JsonSerializer.Deserialize<NativeMeridianJournal>(flip)!.Phase);
        await AssertTerminalStartupNightAsync(fixture, mode);
        await AssertUnissuedFlipArchiveAsync(fixture.Root, flip);
        Assert.Equal(SHA256.HashData(manifest), SHA256.HashData(await File.ReadAllBytesAsync(manifestPath)));
        Assert.Equal(SHA256.HashData(flip), SHA256.HashData(await File.ReadAllBytesAsync(flipPath)));
    }

    private static ObservationRunLockedMetadata NewMeridianStartupMetadata(ObservationRunManifest source) =>
        source.LockedMetadata with { Labels = new Dictionary<string, string>(source.LockedMetadata.Labels!)
            { ["nativeTargetRootRunId"] = "fresh-new-run" } };

    private static async Task AssertUnissuedFlipArchiveAsync(string root, byte[] expected)
    {
        Assert.False(File.Exists(Path.Combine(root, "control", "native-meridian-flip.json")));
        var archive = Assert.Single(Directory.GetFiles(root, "native-meridian-flip.json.pre-dispatch-*.archive", SearchOption.AllDirectories));
        Assert.Equal(expected, await File.ReadAllBytesAsync(archive));
        using var audit = JsonDocument.Parse(await File.ReadAllTextAsync(archive + ".audit.json"));
        foreach (var key in new[] { "motionSent", "flipVerified", "returnVerified", "targetOnSlitVerified", "countersReset", "continuedOldTarget" })
            Assert.False(audit.RootElement.GetProperty(key).GetBoolean());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(expected)), audit.RootElement.GetProperty("originalSha256").GetString());
    }
}
