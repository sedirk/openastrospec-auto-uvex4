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

    [Fact]
    public async Task SlitIlluminationCompletionIsConsumedBeforeNextQueryIsWritten()
    {
        await using var transport = new DelayedSlitCompletionTransport();
        await using var session = new UvexProtocolSession(transport, TimeSpan.FromSeconds(1));
        await session.OpenAsync(CancellationToken.None);

        var off = session.SendAsync(UvexCommands.SlitIlluminationOff(), CancellationToken.None);
        await transport.SlitCommandWritten.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var diagnostics = session.SendAsync(UvexCommands.SlitPhotodiodeValue(), CancellationToken.None);

        await Task.Delay(50);
        Assert.Equal(["SLOF"], transport.WrittenCodes);

        transport.ReleaseSlitCompletion();
        Assert.Equal("SLOF", (await off)!.Code);
        Assert.Equal("29", (await diagnostics)!.Arguments.Single());
        Assert.Equal(["SLOF", "SINT"], transport.WrittenCodes);
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

    private sealed class DelayedSlitCompletionTransport : IUvexTransport
    {
        private readonly Channel<string> frames = Channel.CreateUnbounded<string>();
        private readonly TaskCompletionSource releaseSlitCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsOpen { get; private set; }
        public List<string> WrittenCodes { get; } = [];
        public TaskCompletionSource SlitCommandWritten { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

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
            Assert.True(UvexFrameParser.TryParse(frame, out var command));
            WrittenCodes.Add(command.Code);
            await frames.Writer.WriteAsync(frame, cancellationToken);
            if (command.Code == "SLOF")
            {
                SlitCommandWritten.TrySetResult();
                await releaseSlitCompletion.Task.WaitAsync(cancellationToken);
                await frames.Writer.WriteAsync(":SLOF;#", cancellationToken);
                return;
            }

            Assert.Equal("SINT", command.Code);
            await frames.Writer.WriteAsync(":SINT;29;#", cancellationToken);
        }

        public void ReleaseSlitCompletion() => releaseSlitCompletion.TrySetResult();

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
            releaseSlitCompletion.TrySetResult();
            frames.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
