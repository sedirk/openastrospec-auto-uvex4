using System.Runtime.CompilerServices;
using System.Threading.Channels;
using UvexAdv.Core;
using UvexAdv.Protocol;

namespace UvexAdv.Core.Tests;

public sealed class UvexProtocolSessionTests
{
    [Fact]
    public async Task IgnoresControllerEchoAndReturnsPayloadFrame()
    {
        await using var transport = new EchoTransport();
        await using var session = new UvexProtocolSession(transport, TimeSpan.FromSeconds(1));
        await session.OpenAsync(CancellationToken.None);

        var response = await session.SendAsync(UvexCommands.FirmwareVersion(), CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal("2.3", response.Arguments.Single());
    }

    [Fact]
    public async Task AcceptsSecondIdenticalFrameAsPingResponse()
    {
        await using var transport = new EchoTransport();
        await using var session = new UvexProtocolSession(transport, TimeSpan.FromSeconds(1));
        await session.OpenAsync(CancellationToken.None);

        var response = await session.SendAsync(UvexCommands.Ping(), CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal("ISLV", response.Code);
    }

    private sealed class EchoTransport : IUvexTransport
    {
        private readonly Channel<string> frames = Channel.CreateUnbounded<string>();

        public bool IsOpen { get; private set; }

        public Task OpenAsync(CancellationToken cancellationToken)
        {
            IsOpen = true;
            return Task.CompletedTask;
        }

        public Task CloseAsync(CancellationToken cancellationToken)
        {
            IsOpen = false;
            return Task.CompletedTask;
        }

        public async Task WriteAsync(string frame, CancellationToken cancellationToken)
        {
            await frames.Writer.WriteAsync(frame, cancellationToken);
            Assert.True(UvexFrameParser.TryParse(frame, out var command));
            var response = command.Code switch
            {
                "IVE1" => ":IVE1;2.3;#",
                "ISLV" => ":ISLV;#",
                _ => throw new InvalidOperationException(command.Code),
            };
            await frames.Writer.WriteAsync(response, cancellationToken);
        }

        public async IAsyncEnumerable<string> ReadChunksAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var frame in frames.Reader.ReadAllAsync(cancellationToken))
            {
                yield return frame;
            }
        }

        public ValueTask DisposeAsync()
        {
            IsOpen = false;
            frames.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
