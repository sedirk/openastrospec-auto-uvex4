using UvexAdv.Phd2;

namespace UvexAdv.Phd2.Tests;

public sealed class Phd2ExactLockPositionTests
{
    [Fact]
    public async Task ExactSetUsesFreshPreconditionAndVerifiesWithoutRetry()
    {
        await using var server = new FakePhd2Server(async (session, cancellationToken) =>
        {
            var state = await session.ReadRequestAsync(cancellationToken);
            Assert.Equal("get_app_state", state.GetProperty("method").GetString());
            await session.ReplyResultAsync(state, "Guiding", cancellationToken);

            var shiftEnabled = await session.ReadRequestAsync(cancellationToken);
            Assert.Equal("get_lock_shift_enabled", shiftEnabled.GetProperty("method").GetString());
            await session.ReplyResultAsync(shiftEnabled, false, cancellationToken);

            var before = await session.ReadRequestAsync(cancellationToken);
            Assert.Equal("get_lock_position", before.GetProperty("method").GetString());
            await session.ReplyResultAsync(before, new[] { 100.0, 200.0 }, cancellationToken);

            var set = await session.ReadRequestAsync(cancellationToken);
            Assert.Equal("set_lock_position", set.GetProperty("method").GetString());
            var parameters = set.GetProperty("params");
            Assert.True(parameters.GetProperty("exact").GetBoolean());
            Assert.Equal(108.0, parameters.GetProperty("x").GetDouble());
            Assert.Equal(206.0, parameters.GetProperty("y").GetDouble());
            await session.ReplyResultAsync(set, 0, cancellationToken);

            var verify = await session.ReadRequestAsync(cancellationToken);
            Assert.Equal("get_lock_position", verify.GetProperty("method").GetString());
            await session.ReplyResultAsync(verify, new[] { 108.05, 205.95 }, cancellationToken);
        });
        await using var client = CreateClient(server);
        await client.ConnectAsync(CancellationToken.None);

        var result = await client.SetExactLockPositionAsync(
            new Phd2ExactLockPositionRequest(
                new Phd2Point(100, 200),
                new Phd2Point(108, 206),
                MaximumExpectedCurrentErrorPixels: 0.1,
                MaximumStepPixels: 10.1,
                MaximumVerificationErrorPixels: 0.1),
            CancellationToken.None);

        Assert.True(result.Exact);
        Assert.False(result.RegistryProfileMutated);
        Assert.False(result.AutomaticRetryAllowed);
        Assert.False(result.PhysicalGuideSettled);
        Assert.True(result.RequiresGuideAndSettle);
        Assert.Equal(10, result.StepPixels, 9);
        Assert.Equal(new Phd2Point(108.05, 205.95), result.Verified);
        Assert.Equal(result.Verified, client.Snapshot.LockPosition);
        Assert.Equal(1, server.ReceivedMethods.Count(method => method == "set_lock_position"));
    }

    [Fact]
    public async Task StaleExpectedCurrentPositionFailsBeforeSet()
    {
        await using var server = new FakePhd2Server(async (session, cancellationToken) =>
        {
            var state = await session.ReadRequestAsync(cancellationToken);
            await session.ReplyResultAsync(state, "Guiding", cancellationToken);
            var shiftEnabled = await session.ReadRequestAsync(cancellationToken);
            await session.ReplyResultAsync(shiftEnabled, false, cancellationToken);
            var before = await session.ReadRequestAsync(cancellationToken);
            await session.ReplyResultAsync(before, new[] { 102.0, 200.0 }, cancellationToken);
        });
        await using var client = CreateClient(server);
        await client.ConnectAsync(CancellationToken.None);

        var exception = await Assert.ThrowsAsync<Phd2Exception>(() => client.SetExactLockPositionAsync(
            new Phd2ExactLockPositionRequest(
                new Phd2Point(100, 200),
                new Phd2Point(108, 206),
                0.25,
                10,
                0.1),
            CancellationToken.None));

        Assert.Contains("precondition", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("set_lock_position", server.ReceivedMethods);
    }

    [Fact]
    public async Task OversizeStageFailsBeforeSet()
    {
        await using var server = new FakePhd2Server(async (session, cancellationToken) =>
        {
            var state = await session.ReadRequestAsync(cancellationToken);
            await session.ReplyResultAsync(state, "Guiding", cancellationToken);
            var shiftEnabled = await session.ReadRequestAsync(cancellationToken);
            await session.ReplyResultAsync(shiftEnabled, false, cancellationToken);
            var before = await session.ReadRequestAsync(cancellationToken);
            await session.ReplyResultAsync(before, new[] { 100.0, 200.0 }, cancellationToken);
        });
        await using var client = CreateClient(server);
        await client.ConnectAsync(CancellationToken.None);

        var exception = await Assert.ThrowsAsync<Phd2Exception>(() => client.SetExactLockPositionAsync(
            new Phd2ExactLockPositionRequest(
                new Phd2Point(100, 200),
                new Phd2Point(111, 200),
                0.1,
                10,
                0.1),
            CancellationToken.None));

        Assert.Contains("exceeds", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("set_lock_position", server.ReceivedMethods);
    }

    [Fact]
    public async Task ExactShiftRequiresGuidingState()
    {
        await using var server = new FakePhd2Server(async (session, cancellationToken) =>
        {
            var state = await session.ReadRequestAsync(cancellationToken);
            await session.ReplyResultAsync(state, "Selected", cancellationToken);
        });
        await using var client = CreateClient(server);
        await client.ConnectAsync(CancellationToken.None);

        var exception = await Assert.ThrowsAsync<Phd2Exception>(() => client.SetExactLockPositionAsync(
            new Phd2ExactLockPositionRequest(
                new Phd2Point(100, 200),
                new Phd2Point(101, 200),
                0.1,
                10,
                0.1),
            CancellationToken.None));

        Assert.Contains("Guiding", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("set_lock_position", server.ReceivedMethods);
    }

    [Fact]
    public async Task VerificationMismatchFailsAndDoesNotResendSet()
    {
        await using var server = new FakePhd2Server(async (session, cancellationToken) =>
        {
            var state = await session.ReadRequestAsync(cancellationToken);
            await session.ReplyResultAsync(state, "Guiding", cancellationToken);
            var shiftEnabled = await session.ReadRequestAsync(cancellationToken);
            await session.ReplyResultAsync(shiftEnabled, false, cancellationToken);
            var before = await session.ReadRequestAsync(cancellationToken);
            await session.ReplyResultAsync(before, new[] { 100.0, 200.0 }, cancellationToken);
            var set = await session.ReadRequestAsync(cancellationToken);
            await session.ReplyResultAsync(set, 0, cancellationToken);
            var verify = await session.ReadRequestAsync(cancellationToken);
            await session.ReplyResultAsync(verify, new[] { 104.0, 200.0 }, cancellationToken);
        });
        await using var client = CreateClient(server);
        await client.ConnectAsync(CancellationToken.None);

        var exception = await Assert.ThrowsAsync<Phd2LockPositionReconciliationRequiredException>(() => client.SetExactLockPositionAsync(
            new Phd2ExactLockPositionRequest(
                new Phd2Point(100, 200),
                new Phd2Point(105, 200),
                0.1,
                10,
                0.1),
            CancellationToken.None));

        Assert.Equal(1, server.ReceivedMethods.Count(method => method == "set_lock_position"));
        Assert.False(exception.AutomaticRetryAllowed);
        Assert.True(exception.ReconciliationRequired);
        Assert.True(exception.MutationResponseReceived);
        Assert.Equal(new Phd2Point(104, 200), exception.Observed);
    }

    [Fact]
    public async Task ContinuousLockShiftBlocksExactStageBeforeMutation()
    {
        await using var server = new FakePhd2Server(async (session, cancellationToken) =>
        {
            var state = await session.ReadRequestAsync(cancellationToken);
            await session.ReplyResultAsync(state, "Guiding", cancellationToken);
            var shiftEnabled = await session.ReadRequestAsync(cancellationToken);
            await session.ReplyResultAsync(shiftEnabled, true, cancellationToken);
        });
        await using var client = CreateClient(server);
        await client.ConnectAsync(CancellationToken.None);

        var exception = await Assert.ThrowsAsync<Phd2Exception>(() => client.SetExactLockPositionAsync(
            Request(),
            CancellationToken.None));

        Assert.Contains("continuous lock shifting", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("set_lock_position", server.ReceivedMethods);
    }

    [Fact]
    public async Task CoordinatorPauseBlocksExactStageBeforeAnyRpc()
    {
        await using var server = new FakePhd2Server((_, cancellationToken) =>
            Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
        await using var client = CreateClient(server);
        await client.ConnectAsync(CancellationToken.None);
        client.PauseAutomation();

        await Assert.ThrowsAsync<Phd2AutomationPausedException>(() =>
            client.SetExactLockPositionAsync(Request(), CancellationToken.None));

        Assert.Empty(server.ReceivedMethods);
    }

    [Fact]
    public async Task AmbiguousSetTimeoutRequiresReconciliationAndNeverResends()
    {
        await using var server = new FakePhd2Server(async (session, cancellationToken) =>
        {
            var state = await session.ReadRequestAsync(cancellationToken);
            await session.ReplyResultAsync(state, "Guiding", cancellationToken);
            var shiftEnabled = await session.ReadRequestAsync(cancellationToken);
            await session.ReplyResultAsync(shiftEnabled, false, cancellationToken);
            var before = await session.ReadRequestAsync(cancellationToken);
            await session.ReplyResultAsync(before, new[] { 100.0, 200.0 }, cancellationToken);
            var set = await session.ReadRequestAsync(cancellationToken);
            Assert.Equal("set_lock_position", set.GetProperty("method").GetString());
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
        await using var client = CreateClient(server, TimeSpan.FromMilliseconds(100));
        await client.ConnectAsync(CancellationToken.None);

        var exception = await Assert.ThrowsAsync<Phd2LockPositionReconciliationRequiredException>(() =>
            client.SetExactLockPositionAsync(Request(), CancellationToken.None));

        Assert.False(exception.MutationResponseReceived);
        Assert.False(exception.AutomaticRetryAllowed);
        Assert.Equal(1, server.ReceivedMethods.Count(method => method == "set_lock_position"));
    }

    [Fact]
    public async Task InvalidFreshLockPositionIsRejectedAsProtocolEvidence()
    {
        await using var server = new FakePhd2Server(async (session, cancellationToken) =>
        {
            var get = await session.ReadRequestAsync(cancellationToken);
            await session.ReplyResultAsync(get, new[] { -1.0, 2.0 }, cancellationToken);
        });
        await using var client = CreateClient(server);
        await client.ConnectAsync(CancellationToken.None);

        var exception = await Assert.ThrowsAsync<Phd2Exception>(() =>
            client.GetLockPositionAsync(CancellationToken.None));

        Assert.Contains("negative", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static Phd2ExactLockPositionRequest Request() => new(
        new Phd2Point(100, 200),
        new Phd2Point(101, 200),
        0.1,
        10,
        0.1);

    private static Phd2Client CreateClient(FakePhd2Server server, TimeSpan? commandTimeout = null) => new(new Phd2ClientOptions
    {
        Host = "127.0.0.1",
        Port = server.Port,
        CommandTimeout = commandTimeout ?? TimeSpan.FromSeconds(2),
        EventTimeoutMargin = TimeSpan.FromSeconds(2),
        FileReadyTimeout = TimeSpan.FromSeconds(2),
    });
}
