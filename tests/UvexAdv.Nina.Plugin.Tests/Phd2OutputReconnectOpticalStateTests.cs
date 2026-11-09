using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using UvexAdv.Observatory;
using UvexAdv.Phd2;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class Phd2OutputReconnectOpticalStateTests
{
    [Theory]
    [InlineData("success")]
    [InlineData("stopped-unconfirmed")]
    [InlineData("capturing")]
    [InlineData("motion")]
    [InlineData("foreign-track-run")]
    [InlineData("foreign-track-epoch")]
    [InlineData("foreign-current-epoch")]
    [InlineData("paused")]
    [InlineData("equipment-changed")]
    public async Task ActualRunnerRetiresOnlyVerifiedPriorEpochWithoutReplenishingBudget(string mode)
    {
        // No constructor/device owner, sockets or real hardware are created.
        var runner = (RealObservationStageRunner)RuntimeHelpers.GetUninitializedObject(typeof(RealObservationStageRunner));
        await using var phd = new Phd2Client(new());
        Set(runner, "phd2", phd);
        var equipment = new Phd2Equipment(new("G3M2210M", true), new("On-Step (ASCOM)", true), null, null, null);
        var profile = new Phd2Profile(2, "c11+ccdt67+slit+2210");
        var restored = new Phd2EquipmentReconnectResult(profile, equipment, equipment, 2, true, false, false, null);
        var snapshot = Phd2StateSnapshot.Disconnected with
        { IsConnected = true, ConnectionEpoch = 2, GuideEpoch = 12, AppState = Phd2AppState.Stopped, Profile = profile, Equipment = equipment };
        switch (mode)
        {
            case "stopped-unconfirmed": restored = restored with { ConfirmedStopped = false }; break;
            case "capturing": snapshot = snapshot with { AppState = Phd2AppState.Looping }; break;
            case "motion": restored = restored with { MotionCommandIssued = true }; break;
            case "foreign-current-epoch": snapshot = snapshot with { ConnectionEpoch = 3 }; break;
            case "paused": snapshot = snapshot with { Phd2Paused = true }; break;
            case "equipment-changed": snapshot = snapshot with { Equipment = equipment with { Camera = new("foreign", true) } }; break;
        }
        typeof(Phd2Client).GetField("snapshot", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(phd, snapshot);
        var trackType = typeof(RealObservationStageRunner).GetNestedType("StellarTargetTrackReference", BindingFlags.NonPublic)!;
        var track = RuntimeHelpers.GetUninitializedObject(trackType);
        trackType.GetProperty("RunId")!.SetValue(track, mode == "foreign-track-run" ? "other" : "run");
        trackType.GetProperty("ConnectionEpoch")!.SetValue(track, mode == "foreign-track-epoch" ? 0L : 1L);
        trackType.GetProperty("Path")!.SetValue(track, "immutable-old-frame.fit");
        Set(runner, "stellarTargetTrack", track);
        Set(runner, "phd2OutputReconnectAttempts", 1);
        Set(runner, "qhyCoarseCumulativeArcseconds", 412.5);
        foreach (var name in new[] { "lastG3Field", "phd2SlitPlacementSession", "durableG3AcquisitionMotion", "pendingG3SearchReturn" })
            Set(runner, name, RuntimeHelpers.GetUninitializedObject(Field(name).FieldType));
        var coarseLedger = Field("durableG3AcquisitionMotion").GetValue(runner);
        var coarseReturn = Field("pendingG3SearchReturn").GetValue(runner);
        var context = new ObservationContext(new("run", "test", new("HD 219937", "HD 219937", 0, 30),
            new(31, 121, 20), DateTimeOffset.UtcNow, TimeSpan.FromMinutes(35), new(0, 0, 0), new(), "ATR", "G3", "QHY"));

        // Demonstrate the original production failure, not merely a field assertion.
        var before = await Assert.ThrowsAsync<PhysicalActionGateException>(() => PredictWithoutSeed(runner, context));
        Assert.Equal("G3_TARGET_TRACK_CONTEXT_CHANGED", before.Gate.Code);
        if (mode == "success")
        {
            Assert.Equal("immutable-old-frame.fit", runner.AcceptConfirmedPhd2OutputReconnect("run", 1, restored, "stop-receipt.json"));
            Assert.Null(Field("stellarTargetTrack").GetValue(runner));
            Assert.Null(Field("lastG3Field").GetValue(runner));
            Assert.Null(Field("phd2SlitPlacementSession").GetValue(runner));
            Assert.NotNull(Field("automaticRebuildStopProof").GetValue(runner));
            Assert.Null(await PredictWithoutSeed(runner, context)); // Fresh evidence required, no invented prediction.
        }
        else
        {
            Assert.ThrowsAny<Exception>(() => runner.AcceptConfirmedPhd2OutputReconnect("run", 1, restored, "stop-receipt.json"));
            Assert.Same(track, Field("stellarTargetTrack").GetValue(runner));
            Assert.Null(Field("automaticRebuildStopProof").GetValue(runner));
        }
        Assert.Same(coarseLedger, Field("durableG3AcquisitionMotion").GetValue(runner));
        Assert.Same(coarseReturn, Field("pendingG3SearchReturn").GetValue(runner));
        Assert.Equal(1, Field("phd2OutputReconnectAttempts").GetValue(runner));
        Assert.Equal(412.5, Field("qhyCoarseCumulativeArcseconds").GetValue(runner));
    }

    [Theory]
    [InlineData("skip", false)]
    [InlineData("cleanup-failure", false)]
    [InlineData("end-policy", false)]
    [InlineData("skip", true)]
    public async Task ProductionRecoveryClassificationReachesActualNextNativeTargetOnlyAfterSafeQualityStop(string boundary, bool hardwareFault)
    {
        var gate = hardwareFault ? GateResult.Unknown("G3_TARGET_TRACK_CONTEXT_CHANGED", "Unexpected external owner")
            : GateResult.TargetQuality("G3_BOUNDED_SEARCH_EXHAUSTED_RETURNED", "New optical measurement exhausted, original return verified",
                new Dictionary<string, double> { ["chargedActions"] = 10 });
        var result = RealObservationStageRunner.Phd2OutputRecoveryFailure(new PhysicalActionGateException(gate), "evidence.json");
        Assert.Equal(gate.Code, result.Gate.Code);
        Assert.Equal(gate.FailureScope, result.Gate.FailureScope);
        Assert.Same(gate.Metrics, result.Gate.Metrics);
        Assert.Equal("evidence.json", result.EvidencePath);
        await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync(hardwareFault ? "unknown-gate" : boundary, gate.Code, result.Gate,
            ObservationStage.PlaceTargetOnSlit);
        Assert.Equal(GateFailureScope.Run, RealObservationStageRunner.Phd2OutputRecoveryFailure(
            new IOException("No verified reconnect"), null).Gate.FailureScope);
    }

    [Fact]
    public async Task RecordedOctober30ReconnectAndStaleTrackAreConsistentAndInputsUnchanged()
    {
        var root = Environment.GetEnvironmentVariable("UVEX_REPLAY_OBSERVATIONS");
        if (string.IsNullOrWhiteSpace(root)) return;
        var directory = Path.Combine(root, "UVEX-20261030T105147Z-6450c708d83147e", "evidence");
        var paths = new[] { "00126-*json", "00194-*json", "00198-*json", "00107-*fit", "00266-*fit" }
            .Select(pattern => Assert.Single(Directory.GetFiles(directory, pattern))).ToArray();
        var hashes = paths.Select(p => SHA256.HashData(File.ReadAllBytes(p))).ToArray();
        using var reconnect = JsonDocument.Parse(await File.ReadAllTextAsync(paths[2]));
        var receipt = reconnect.RootElement.GetProperty("payload").GetProperty("restored");
        Assert.Equal(2, receipt.GetProperty("connectionEpoch").GetInt64());
        Assert.True(receipt.GetProperty("confirmedStopped").GetBoolean());
        Assert.False(receipt.GetProperty("captureStarted").GetBoolean());
        Assert.False(receipt.GetProperty("motionCommandIssued").GetBoolean());
        using var failed = JsonDocument.Parse(await File.ReadAllTextAsync(paths[1]));
        var failure = failed.RootElement.GetProperty("payload").GetProperty("failure");
        Assert.True(failure.GetProperty("failed").GetBoolean());
        Assert.Equal(3, failure.GetProperty("consecutiveMissingOutputs").GetInt32());
        Assert.InRange(failure.GetProperty("lastStep").GetProperty("snr").GetDouble(), 109, 110);
        await ActualRunnerRetiresOnlyVerifiedPriorEpochWithoutReplenishingBudget("success");
        for (var i = 0; i < paths.Length; i++) Assert.Equal(hashes[i], SHA256.HashData(File.ReadAllBytes(paths[i])));
    }

    private static async Task<object?> PredictWithoutSeed(RealObservationStageRunner runner, ObservationContext context)
    {
        var method = typeof(RealObservationStageRunner).GetMethod("PredictStellarTargetTrackAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var task = (Task)method.Invoke(runner, new object?[]
        { context, null, null, null, null, null, null, "new.fit", "new-hash", null, CancellationToken.None })!;
        await task;
        return task.GetType().GetProperty("Result")!.GetValue(task);
    }
    private static FieldInfo Field(string name) => typeof(RealObservationStageRunner).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static void Set(object value, string name, object? fieldValue) => Field(name).SetValue(value, fieldValue);
}
