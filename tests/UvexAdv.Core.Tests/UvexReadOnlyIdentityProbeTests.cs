using System.Runtime.CompilerServices;
using System.Threading.Channels;
using UvexAdv.Core;
using UvexAdv.Protocol;

namespace UvexAdv.Core.Tests;

public sealed class UvexReadOnlyIdentityProbeTests
{
    [Fact]
    public async Task VerifiedProbeOnlySendsTwoIdentityQueriesAndCloses()
    {
        var transport = new ProbeTransport();
        await using (var session = new UvexProtocolSession(transport, TimeSpan.FromMilliseconds(100)))
        {
            await session.OpenAsync(CancellationToken.None);
            var identity = await UvexReadOnlyIdentityProbe.ReadAsync(session, CancellationToken.None);
            Assert.Equal("2.3", identity.FirmwareVersion);
            Assert.Equal("Microcontoler Arduino for UVEX4", identity.Description);
            Assert.Equal(["IVE1", "IDE1"], transport.Commands);
        }
        Assert.False(transport.IsOpen);
    }

    [Fact]
    public async Task WrongDeviceIsNotAccepted()
    {
        var transport = new ProbeTransport(description: "CH340 roof controller");
        await using (var session = new UvexProtocolSession(transport, TimeSpan.FromMilliseconds(100)))
        {
            await session.OpenAsync(CancellationToken.None);
            await Assert.ThrowsAsync<InvalidOperationException>(() => UvexReadOnlyIdentityProbe.ReadAsync(session, CancellationToken.None));
            Assert.Equal(["IVE1", "IDE1"], transport.Commands);
        }
        Assert.False(transport.IsOpen);
    }

    [Fact]
    public async Task EchoIsNotIdentityAndTimeoutStopsBeforeSecondQuery()
    {
        var transport = new ProbeTransport(echoOnly: true);
        await using (var session = new UvexProtocolSession(transport, TimeSpan.FromMilliseconds(30)))
        {
            await session.OpenAsync(CancellationToken.None);
            await Assert.ThrowsAsync<TimeoutException>(() => UvexReadOnlyIdentityProbe.ReadAsync(session, CancellationToken.None));
            Assert.Equal(["IVE1"], transport.Commands);
        }
        Assert.False(transport.IsOpen);
    }

    [Fact]
    public async Task CancellationClosesAndNeverInitializes()
    {
        var transport = new ProbeTransport(echoOnly: true);
        await using (var session = new UvexProtocolSession(transport, TimeSpan.FromSeconds(3)))
        {
            await session.OpenAsync(CancellationToken.None);
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => UvexReadOnlyIdentityProbe.ReadAsync(session, cts.Token));
            Assert.Equal(["IVE1"], transport.Commands);
        }
        Assert.False(transport.IsOpen);
    }

    private sealed class ProbeTransport(string description = "Microcontoler Arduino for UVEX4", bool echoOnly = false) : IUvexTransport
    {
        private readonly Channel<string> frames = Channel.CreateUnbounded<string>();
        public List<string> Commands { get; } = [];
        public bool IsOpen { get; private set; }
        public Task OpenAsync(CancellationToken cancellationToken) { IsOpen = true; return Task.CompletedTask; }
        public Task CloseAsync(CancellationToken cancellationToken) { IsOpen = false; return Task.CompletedTask; }
        public async Task WriteAsync(string frame, CancellationToken cancellationToken)
        {
            Assert.True(UvexFrameParser.TryParse(frame, out var query));
            Commands.Add(query.Code);
            await frames.Writer.WriteAsync(frame, cancellationToken);
            if (!echoOnly) await frames.Writer.WriteAsync(query.Code == "IVE1" ? ":IVE1;2.3;#" : $":IDE1;{description};#", cancellationToken);
        }
        public async IAsyncEnumerable<string> ReadChunksAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var frame in frames.Reader.ReadAllAsync(cancellationToken)) yield return frame;
        }
        public ValueTask DisposeAsync() { IsOpen = false; return ValueTask.CompletedTask; }
    }
}
