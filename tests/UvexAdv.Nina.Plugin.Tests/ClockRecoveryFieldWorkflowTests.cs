using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class ClockRecoveryFieldWorkflowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewWorkflowWithAlreadyAttemptedReceiptMakesNoPortCallsEvenIfThePriorAttemptWasRejected(bool rejected)
    {
        var receipt = CreateReceipt();
        var at = DateTimeOffset.UtcNow;
        var previous = Snapshot(at);
        if (rejected) previous = previous with { OwnersStoppedAndJoined = false };
        Assert.Equal(!rejected, receipt.TryConsume("previous-continuation", previous, at, out _));
        Assert.True(receipt.Attempted);
        var port = StrictWorkflowPort.Create<IClockRecoveryFieldPort>(out var trace);
        var workflow = new ClockRecoveryFieldWorkflow(receipt, port);
        var context = AtrWorkflowFixture.CreateContext();

        // This can be a later focus/meridian segment. Its own canonical gates
        // decide readiness; the consumed optional receipt must not query owners,
        // inject old stopped-only prerequisites, capture, or write new evidence.
        foreach (var stage in ObservationRunCoordinator.Stages)
            Assert.Null(await workflow.TryExecuteAsync(stage, context, default));
        Assert.Empty(trace.Calls);
        Assert.Equal(rejected ? null : "previous-continuation", receipt.ConsumedRunId);
    }

    [Fact]
    public async Task AcceptedReceiptWithinTheSameWorkflowStillRevalidatesEachSubstitutedMarker()
    {
        var receipt = CreateReceipt();
        var port = StrictWorkflowPort.Create<IClockRecoveryFieldPort>(out var trace);
        trace.Handlers[nameof(IClockRecoveryFieldPort.ReadSnapshot)] = _ => Snapshot(DateTimeOffset.UtcNow);
        trace.Handlers[nameof(IClockRecoveryFieldPort.ValidateStoppedBoundaryAsync)] = _ =>
            Task.FromResult(GateResult.Pass("STOPPED", "finite owner"));
        trace.Handlers[nameof(IClockRecoveryFieldPort.PersistAttemptAsync)] = _ => Task.CompletedTask;
        trace.Handlers[nameof(IClockRecoveryFieldPort.CaptureFreshAsync)] = _ => Task.FromResult(
            new ClockRecoveryFieldProbeResult(new(GateResult.Pass("FRESH_G3", "new segment evidence"), "new-frame"), false));
        trace.Handlers[nameof(IClockRecoveryFieldPort.ValidateFreshFieldAsync)] = _ =>
            Task.FromResult(GateResult.Pass("FRESH_BINDING", "finite binding"));
        trace.Handlers[nameof(IClockRecoveryFieldPort.RecordDecisionAsync)] = _ => Task.CompletedTask;
        var workflow = new ClockRecoveryFieldWorkflow(receipt, port);
        var context = AtrWorkflowFixture.CreateContext();
        foreach (var stage in new[] { ObservationStage.SlewToCatalogTarget, ObservationStage.AcquireQhyWideField,
            ObservationStage.CoarseCenter, ObservationStage.AcquireG3SlitField })
        {
            var result = await workflow.TryExecuteAsync(stage, context, default);
            Assert.NotNull(result);
            Assert.True(result.CanAdvance);
            Assert.Equal("CLOCK_CURRENT_FIELD_PREREQUISITE_REVALIDATED", result.Gate.Code);
        }
        Assert.True(receipt.Attempted);
        Assert.Equal(context.Plan.ObservationRunId, receipt.ConsumedRunId);
        Assert.Equal(4, trace.Calls.Count(x => x == nameof(IClockRecoveryFieldPort.ValidateStoppedBoundaryAsync)));
        Assert.Equal(4, trace.Calls.Count(x => x == nameof(IClockRecoveryFieldPort.ValidateFreshFieldAsync)));
        Assert.Single(trace.Calls, x => x == nameof(IClockRecoveryFieldPort.ReadSnapshot));
        Assert.Single(trace.Calls, x => x == nameof(IClockRecoveryFieldPort.PersistAttemptAsync));
        Assert.Single(trace.Calls, x => x == nameof(IClockRecoveryFieldPort.CaptureFreshAsync));
    }

    [Fact]
    public async Task UnusedReceiptStillReturnsTheLiveHardBoundaryWithoutConsumptionOrCapture()
    {
        var receipt = CreateReceipt();
        var port = StrictWorkflowPort.Create<IClockRecoveryFieldPort>(out var trace);
        var boundary = GateResult.Unknown("CLOCK_CURRENT_FIELD_OWNER_CHANGED", "external owner is active");
        trace.Handlers[nameof(IClockRecoveryFieldPort.ValidateStoppedBoundaryAsync)] = _ => Task.FromResult(boundary);
        var result = await new ClockRecoveryFieldWorkflow(receipt, port).TryExecuteAsync(
            ObservationStage.SlewToCatalogTarget, AtrWorkflowFixture.CreateContext(), default);
        Assert.NotNull(result);
        Assert.Same(boundary, result.Gate);
        Assert.False(result.CanAdvance);
        Assert.False(receipt.Attempted);
        Assert.Equal(nameof(IClockRecoveryFieldPort.ValidateStoppedBoundaryAsync), Assert.Single(trace.Calls));
    }

    private static ClockRecoveryPositionSnapshot Snapshot(DateTimeOffset at) => new(new(
        new("star", "HD", 52, 46), new string('A', 64), new string('B', 64),
        "finite-mount", Guid.Parse("79fcd5e0-97d4-48c6-a67a-111430348bf0"), "localhost:4400", 1, "finite-guide", "G3"),
        new(52, 46, "JNOW", "pierEast", at), true, true, true, true);

    private static ClockRecoveryPositionCheck CreateReceipt()
    {
        var now = DateTimeOffset.UtcNow;
        return Assert.IsType<ClockRecoveryPositionCheck>(ClockRecoveryPositionCheck.TryCreate("original",
            GateResult.Pass("MOUNT_CLOCK_RECOVERED_BY_NINA_RECONNECT", "finite repaired readback"),
            Snapshot(now.AddSeconds(-1)), Snapshot(now), now, now.AddMinutes(3), 2, out _));
    }
}
