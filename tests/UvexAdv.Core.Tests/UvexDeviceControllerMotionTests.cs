using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using UvexAdv.Core;
using UvexAdv.Protocol;

namespace UvexAdv.Core.Tests;

public sealed class UvexDeviceControllerMotionTests
{
    [Fact]
    public async Task SlitMoveUsesUnsolicitedBusyTransitionAndRefreshesLivePosition()
    {
        await using var transport = new MotionTransport(completeMotion: true);
        await using var session = new UvexProtocolSession(transport, TimeSpan.FromSeconds(1));
        var leases = new ControlLeaseManager();
        using var controller = new UvexDeviceController(session, CreateOptions(TimeSpan.FromSeconds(1)), leases);

        await controller.ConnectAsync(CancellationToken.None);
        var lease = leases.Acquire("motion-test", TimeSpan.FromSeconds(30));

        await controller.SelectSlitAsync(1, lease.Token, CancellationToken.None);

        Assert.Equal(DeviceConnectionState.Ready, controller.Status.ConnectionState);
        Assert.Equal(1, controller.Status.SlitPosition);
        Assert.Equal(1000, controller.Status.SlitMotorPositionSteps);
        Assert.True(controller.Status.PositionKnown);
        Assert.Equal(UvexPositionTrust.Live, controller.Status.PositionTrust);
        Assert.Contains("SMOV", transport.WrittenCodes);
        Assert.DoesNotContain("IBSY", transport.WrittenCodes);
        Assert.DoesNotContain("SSTP", transport.WrittenCodes);
    }

    [Fact]
    public async Task MissingBusyCompletionTimesOutAndRequestsStopWithoutQueryingBusy()
    {
        await using var transport = new MotionTransport(completeMotion: false);
        await using var session = new UvexProtocolSession(transport, TimeSpan.FromSeconds(1));
        var leases = new ControlLeaseManager();
        using var controller = new UvexDeviceController(session, CreateOptions(TimeSpan.FromMilliseconds(80)), leases);

        await controller.ConnectAsync(CancellationToken.None);
        var lease = leases.Acquire("motion-timeout-test", TimeSpan.FromSeconds(30));

        var error = await Assert.ThrowsAsync<TimeoutException>(
            () => controller.SelectSlitAsync(1, lease.Token, CancellationToken.None));

        Assert.Contains("IBSY;0 -> IBSY;1", error.Message, StringComparison.Ordinal);
        Assert.Equal(DeviceConnectionState.Faulted, controller.Status.ConnectionState);
        Assert.False(controller.Status.PositionKnown);
        Assert.Contains("SMOV", transport.WrittenCodes);
        Assert.Contains("SSTP", transport.WrittenCodes);
        Assert.DoesNotContain("IBSY", transport.WrittenCodes);
    }

    [Fact]
    public async Task BusyCompletionWithoutRequestedSlitReadbackFailsClosed()
    {
        await using var transport = new MotionTransport(completeMotion: true, applyRequestedSlit: false);
        await using var session = new UvexProtocolSession(transport, TimeSpan.FromSeconds(1));
        var leases = new ControlLeaseManager();
        using var controller = new UvexDeviceController(session, CreateOptions(TimeSpan.FromSeconds(1)), leases);

        await controller.ConnectAsync(CancellationToken.None);
        var lease = leases.Acquire("readback-mismatch-test", TimeSpan.FromSeconds(30));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => controller.SelectSlitAsync(1, lease.Token, CancellationToken.None));

        Assert.Contains("Slit readback 2 does not match requested position 1", error.Message, StringComparison.Ordinal);
        Assert.Equal(DeviceConnectionState.Faulted, controller.Status.ConnectionState);
        Assert.False(controller.Status.PositionKnown);
        Assert.Contains("SSTP", transport.WrittenCodes);
        Assert.DoesNotContain("IBSY", transport.WrittenCodes);
    }

    private static UvexSafetyOptions CreateOptions(TimeSpan motionTimeout) => new()
    {
        Simulator = true,
        PortName = "COM5",
        SerialOpenDelay = TimeSpan.Zero,
        SerialPostMotionSettleDelay = TimeSpan.Zero,
        CommandTimeout = TimeSpan.FromSeconds(1),
        MotionTimeout = motionTimeout,
    };

    private sealed class MotionTransport(bool completeMotion, bool applyRequestedSlit = true) : IUvexTransport
    {
        private readonly Channel<string> frames = Channel.CreateUnbounded<string>();
        private int slitPosition = 2;
        private int slitMotorPosition = -512;

        public bool IsOpen { get; private set; }

        public ConcurrentQueue<string> WrittenCodes { get; } = new();

        public Task OpenAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IsOpen = true;
            return Task.CompletedTask;
        }

        public Task CloseAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IsOpen = false;
            return Task.CompletedTask;
        }

        public async Task WriteAsync(string frame, CancellationToken cancellationToken)
        {
            Assert.True(IsOpen);
            Assert.True(UvexFrameParser.TryParse(frame, out var command));
            WrittenCodes.Enqueue(command.Code);

            // Firmware echoes every command. Motion completion is deliberately
            // emitted inside WriteAsync to exercise the subscribe-before-send
            // race that exists with short or no-op moves.
            await frames.Writer.WriteAsync(frame, cancellationToken);
            if (command.Code == "SMOV")
            {
                await frames.Writer.WriteAsync(":IBSY;0;#", cancellationToken);
                if (completeMotion)
                {
                    if (applyRequestedSlit)
                    {
                        slitPosition = 1;
                        slitMotorPosition = 1000;
                    }

                    await frames.Writer.WriteAsync(":STEP;1000;#", cancellationToken);
                    await frames.Writer.WriteAsync(":IBSY;1;#", cancellationToken);
                }

                return;
            }

            var response = command.Code switch
            {
                "ISLV" => ":ISLV;#",
                "IVE1" => ":IVE1;2.3-test;#",
                "IDE1" => ":IDE1;UVEX4 test controller;#",
                "IST0" => ":IST0;447;#",
                "SMAX" => ":SMAX;4;#",
                "SNAM" => ":SNAM;300um;15um;25um;35um;#",
                "SGOF" => $":SGOF;{command.Arguments[0]};0;#",
                "SINT" => ":SINT;29;#",
                "SGTS" => ":SGTS;283;#",
                "SGPH" => ":SGPH;1;#",
                "GPOS" => ":GPOS;-1923;5591.50;3828.35;7354.65;#",
                "FPOS" => ":FPOS;12500;#",
                "SPOS" => $":SPOS;{slitPosition};#",
                "STEP" => $":STEP;{slitMotorPosition};#",
                _ => null,
            };

            if (response is not null)
            {
                await frames.Writer.WriteAsync(response, cancellationToken);
            }
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
