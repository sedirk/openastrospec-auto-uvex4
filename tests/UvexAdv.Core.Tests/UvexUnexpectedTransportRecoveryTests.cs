using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using UvexAdv.Core;
using UvexAdv.Protocol;

namespace UvexAdv.Core.Tests;

public sealed class UvexUnexpectedTransportRecoveryTests
{
    [Fact]
    public async Task UnexpectedClosedTransportInvalidatesLiveStateAndReopensWithFreshReadback()
    {
        await using var transport = new RecoverableTransport();
        await using var session = new UvexProtocolSession(transport, TimeSpan.FromSeconds(1));
        using var controller = new UvexDeviceController(session, Options(), new ControlLeaseManager());

        await controller.ConnectAsync(CancellationToken.None);
        var measuredBeforeLoss = controller.Status.PositionMeasuredUtc;
        transport.DropUnexpectedly();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => controller.RefreshAsync(CancellationToken.None));

        Assert.Contains("COM5 is closed", error.Message, StringComparison.Ordinal);
        Assert.True(controller.UnexpectedTransportRecoveryPending);
        Assert.Equal(DeviceConnectionState.Faulted, controller.Status.ConnectionState);
        Assert.False(controller.Status.PositionKnown);
        Assert.Equal(UvexPositionTrust.LastKnown, controller.Status.PositionTrust);
        Assert.Equal(UvexOutputState.Unknown, controller.Status.SlitIlluminationLedState);

        Assert.True(await controller.TryRecoverUnexpectedTransportLossAsync(CancellationToken.None));

        Assert.False(controller.UnexpectedTransportRecoveryPending);
        Assert.Equal(DeviceConnectionState.Ready, controller.Status.ConnectionState);
        Assert.True(controller.Status.PositionKnown);
        Assert.Equal(UvexPositionTrust.Live, controller.Status.PositionTrust);
        Assert.True(controller.Status.PositionMeasuredUtc >= measuredBeforeLoss);
        Assert.Equal(2, transport.OpenCount);
        Assert.Equal(2, transport.WrittenCodes.Count(code => code == "ISLV"));
    }

    [Fact]
    public async Task ManualDisconnectNeverArmsOrPerformsAutomaticRecovery()
    {
        await using var transport = new RecoverableTransport();
        await using var session = new UvexProtocolSession(transport, TimeSpan.FromSeconds(1));
        using var controller = new UvexDeviceController(session, Options(), new ControlLeaseManager());

        await controller.ConnectAsync(CancellationToken.None);
        await controller.DisconnectAsync(CancellationToken.None);

        Assert.False(controller.UnexpectedTransportRecoveryPending);
        Assert.Equal(DeviceConnectionState.Disconnected, controller.Status.ConnectionState);
        Assert.False(await controller.TryRecoverUnexpectedTransportLossAsync(CancellationToken.None));
        Assert.Equal(1, transport.OpenCount);
    }

    [Fact]
    public async Task ClosedTransportIsRejectedBeforeAnyLedCommandIsWritten()
    {
        await using var transport = new RecoverableTransport();
        await using var session = new UvexProtocolSession(transport, TimeSpan.FromSeconds(1));
        var leases = new ControlLeaseManager();
        using var controller = new UvexDeviceController(session, Options(), leases);

        await controller.ConnectAsync(CancellationToken.None);
        var lease = leases.Acquire("transport-loss-test", TimeSpan.FromSeconds(30));
        transport.DropUnexpectedly();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => controller.SetSlitIlluminationAsync(false, lease.Token, CancellationToken.None));

        Assert.DoesNotContain("SLOF", transport.WrittenCodes);
        Assert.True(controller.UnexpectedTransportRecoveryPending);
        Assert.Equal(DeviceConnectionState.Faulted, controller.Status.ConnectionState);
    }

    private static UvexSafetyOptions Options() => new()
    {
        Simulator = true,
        PortName = "COM5",
        SerialOpenDelay = TimeSpan.Zero,
        SerialPostMotionSettleDelay = TimeSpan.Zero,
        CommandTimeout = TimeSpan.FromSeconds(1),
        MotionTimeout = TimeSpan.FromSeconds(1),
    };

    private sealed class RecoverableTransport : IUvexTransport
    {
        private readonly Channel<string> frames = Channel.CreateUnbounded<string>();

        public bool IsOpen { get; private set; }
        public int OpenCount { get; private set; }
        public ConcurrentQueue<string> WrittenCodes { get; } = new();

        public Task OpenAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IsOpen = true;
            OpenCount++;
            return Task.CompletedTask;
        }

        public Task CloseAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IsOpen = false;
            return Task.CompletedTask;
        }

        public void DropUnexpectedly() => IsOpen = false;

        public async Task WriteAsync(string frame, CancellationToken cancellationToken)
        {
            if (!IsOpen)
            {
                throw new InvalidOperationException("The port is closed.");
            }

            Assert.True(UvexFrameParser.TryParse(frame, out var command));
            WrittenCodes.Enqueue(command.Code);
            await frames.Writer.WriteAsync(frame, cancellationToken);
            var response = command.Code switch
            {
                "ISLV" => ":ISLV;#",
                "IVE1" => ":IVE1;2.3-test;#",
                "IDE1" => ":IDE1;UVEX4 recovery test;#",
                "IST0" => ":IST0;447;#",
                "SMAX" => ":SMAX;4;#",
                "SNAM" => ":SNAM;300um;15um;25um;35um;#",
                "SGOF" => $":SGOF;{command.Arguments[0]};0;#",
                "GPOS" => ":GPOS;-1923;5591.50;3828.35;7354.65;#",
                "FPOS" => ":FPOS;12500;#",
                "SPOS" => ":SPOS;2;#",
                "STEP" => ":STEP;-512;#",
                "SINT" => ":SINT;30;#",
                "SGTS" => ":SGTS;283;#",
                "SGPH" => ":SGPH;1;#",
                "ITEM" => ":ITEM;36.00;#",
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
