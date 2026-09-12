using System.Diagnostics;
using System.ServiceProcess;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using UvexAdv.Core;
using UvexAdv.Service.Persistence;

namespace UvexAdv.Service.Transport;

/// <summary>Explicit maintenance entry point of the sole UVEX owner, not background discovery.</summary>
internal static class SerialPortIdentificationCommand
{
    public static async Task RunAsync(string[] args)
    {
        if (args.Length != 3 || args[2] != "--confirm-read-only")
            throw new InvalidOperationException("Usage: UvexAdv.Service --identify-serial-ports COMx,COMy --confirm-read-only (Windows UVEX service must be stopped).");
        var ports = args[1].Split(',', StringSplitOptions.TrimEntries);
        if (ports.Length is < 1 or > 8 || ports.Distinct(StringComparer.OrdinalIgnoreCase).Count() != ports.Length)
            throw new InvalidOperationException("Identify one to eight distinct, explicitly requested serial ports.");
        foreach (var port in ports) new UvexSafetyOptions { PortName = port }.ValidatePortName();
        // No other driver/service is stopped, and access-denied is never retried.
        using var service = new ServiceController("UVEX-ADV");
        if (service.Status != ServiceControllerStatus.Stopped)
            throw new InvalidOperationException("UVEX_SERIAL_OWNER_ACTIVE: Stop the installed UVEX service before maintenance identification.");
        if (Process.GetProcesses().Any(process => process.ProcessName.Equals("DRIVER.UVEX4", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("UVEX_SERIAL_VENDOR_OWNER_ACTIVE: Close the vendor UVEX application before identification.");

        using var config = JsonDocument.Parse(File.ReadAllText(new UvexDataPaths().Configuration));
        var declared = config.RootElement.GetProperty("Uvex").Deserialize<UvexSafetyOptions>(
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("UVEX serial configuration is missing.");

        var results = new List<object>();
        foreach (var port in ports)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            UvexSerialPortCandidate? candidate = null;
            try
            {
                var present = WindowsSerialPortInventory.GetPresentPorts(declared.ExpectedUsbVid, declared.ExpectedUsbPid);
                candidate = present.SingleOrDefault(value => value.PortName.Equals(port, StringComparison.OrdinalIgnoreCase));
                var options = new UvexSafetyOptions
                {
                    PortName = port, Simulator = false,
                    ExpectedUsbInstanceId = candidate?.InstanceId ?? string.Empty,
                    ExpectedUsbVid = declared.ExpectedUsbVid, ExpectedUsbPid = declared.ExpectedUsbPid,
                    ReservedSerialPorts = declared.ReservedSerialPorts,
                };
                WindowsSerialPortInventory.RequireCandidate(options, present, WindowsSerialPortInventory.GetReservedPorts());
                await using var session = new UvexProtocolSession(
                    new SerialUvexTransport(options, NullLogger<SerialUvexTransport>.Instance), TimeSpan.FromSeconds(2));
                await session.OpenAsync(timeout.Token).ConfigureAwait(false);
                var identity = await UvexReadOnlyIdentityProbe.ReadAsync(session, timeout.Token).ConfigureAwait(false);
                results.Add(new { PortName = port, candidate!.InstanceId, Verified = true, Identity = identity });
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or System.TimeoutException or OperationCanceledException)
            {
                results.Add(new { PortName = port, InstanceId = candidate?.InstanceId, Verified = false, Error = ex.Message });
            }
        }
        Console.WriteLine(JsonSerializer.Serialize(new { ReadOnly = true, Results = results }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
