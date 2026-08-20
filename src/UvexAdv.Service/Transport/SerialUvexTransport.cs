using System.IO.Ports;
using System.Runtime.CompilerServices;
using System.Text;
using UvexAdv.Core;
using UvexAdv.Protocol;

namespace UvexAdv.Service.Transport;

internal sealed class SerialUvexTransport(UvexSafetyOptions options, ILogger<SerialUvexTransport> logger) : IUvexTransport
{
    private SerialPort? port;

    public bool IsOpen => port?.IsOpen == true;

    public async Task OpenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (port?.IsOpen == true)
        {
            throw new InvalidOperationException("The verified UVEX serial port is already open in this process.");
        }

        if (!options.PortName.Equals("COM5", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The production transport is locked to COM5.");
        }

        options.HardwareIdentityVerified = WindowsUsbIdentityVerifier.MatchesPort(
            options.PortName,
            options.ExpectedUsbVid,
            options.ExpectedUsbPid);
        if (!options.HardwareIdentityVerified)
        {
            throw new InvalidOperationException(
                $"{options.PortName} does not match VID_{options.ExpectedUsbVid}&PID_{options.ExpectedUsbPid}; serial open was blocked.");
        }

        var candidate = new SerialPort(options.PortName, 115200, Parity.None, 8, StopBits.One)
        {
            Encoding = Encoding.ASCII,
            DtrEnable = false,
            RtsEnable = false,
            ReadTimeout = 1000,
            WriteTimeout = 1000,
            NewLine = "#",
        };
        try
        {
            candidate.Open();
            port = candidate;
            logger.LogInformation("Opened verified UVEX serial port {PortName} at 115200 8N1", options.PortName);
            if (options.SerialOpenDelay > TimeSpan.Zero)
            {
                await Task.Delay(options.SerialOpenDelay, cancellationToken).ConfigureAwait(false);
            }

            candidate.DiscardInBuffer();
            candidate.DiscardOutBuffer();
        }
        catch
        {
            if (ReferenceEquals(port, candidate))
            {
                port = null;
            }

            candidate.Dispose();
            throw;
        }
    }

    public Task CloseAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (port is not null)
        {
            if (port.IsOpen)
            {
                port.Close();
            }

            port.Dispose();
            port = null;
        }

        return Task.CompletedTask;
    }

    public Task WriteAsync(string frame, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var serial = port ?? throw new InvalidOperationException("Serial port is not open.");
        logger.LogDebug("UVEX TX {Frame}", frame);
        serial.Write(frame);
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<string> ReadChunksAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var serial = port ?? throw new InvalidOperationException("Serial port is not open.");
            var chunk = serial.ReadExisting();
            if (chunk.Length == 0)
            {
                await Task.Delay(20, cancellationToken).ConfigureAwait(false);
                continue;
            }

            logger.LogDebug("UVEX RX {Chunk}", chunk);
            yield return chunk;
        }
    }

    public async ValueTask DisposeAsync() => await CloseAsync(CancellationToken.None).ConfigureAwait(false);
}
