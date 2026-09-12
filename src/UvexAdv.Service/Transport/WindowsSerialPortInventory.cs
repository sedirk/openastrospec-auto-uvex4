using Microsoft.Win32;
using System.Runtime.InteropServices;
using UvexAdv.Core;

namespace UvexAdv.Service.Transport;

public sealed record UvexSerialPortCandidate(string PortName, string InstanceId);
public sealed record ReservedSerialPort(string PortName, string Owner);

public static class WindowsSerialPortInventory
{
    // CM_LOCATE_DEVNODE_NORMAL excludes phantom entries left by unplugged devices.
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

    public static IReadOnlyList<UvexSerialPortCandidate> GetPresentPorts(string expectedVid, string expectedPid)
    {
        var result = new List<UvexSerialPortCandidate>();
        using var usb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB", writable: false);
        if (usb is null) return result;
        foreach (var name in usb.GetSubKeyNames().Where(name =>
                     name.Split('&').Contains($"VID_{expectedVid}", StringComparer.OrdinalIgnoreCase) &&
                     name.Split('&').Contains($"PID_{expectedPid}", StringComparer.OrdinalIgnoreCase)))
        {
            using var device = usb.OpenSubKey(name, writable: false);
            if (device is null) continue;
            foreach (var instance in device.GetSubKeyNames())
            {
                var id = $@"USB\{name}\{instance}";
                if (CM_Locate_DevNodeW(out _, id, 0) != 0) continue;
                using var parameters = device.OpenSubKey(instance + @"\Device Parameters", writable: false);
                if (parameters?.GetValue("PortName") is string port)
                    result.Add(new UvexSerialPortCandidate(port.ToUpperInvariant(), id));
            }
        }
        return result;
    }

    public static IReadOnlyList<ReservedSerialPort> GetReservedPorts()
    {
        var result = new List<ReservedSerialPort>();
        // Inspect both ASCOM registry views. Access failure propagates: inability
        // to exclude a roof/controller is not permission to send it UVEX queries.
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var ascom = root.OpenSubKey(@"SOFTWARE\ASCOM", writable: false);
            if (ascom is null) continue;
            foreach (var category in ascom.GetSubKeyNames().Where(name => name.EndsWith(" Drivers", StringComparison.Ordinal)))
            {
                using var drivers = ascom.OpenSubKey(category, writable: false);
                if (drivers is not null) ReadReservations(drivers, category, result, 0);
            }
        }
        return result.Distinct().ToArray();
    }

    private static void ReadReservations(RegistryKey key, string owner, List<ReservedSerialPort> result, int depth)
    {
        if (depth > 8) throw new InvalidOperationException("UVEX_SERIAL_RESERVATION_DEPTH: ASCOM serial ownership cannot be fully inspected.");
        foreach (var name in key.GetValueNames())
        {
            // Read only port fields, never ASCOM credentials or unrelated settings.
            var field = name.Length == 0 ? key.Name.Split('\\').Last() : name;
            var normalized = field.Replace(" ", "").Replace("_", "").ToLowerInvariant();
            if (normalized is not ("port" or "comport" or "serialport" or "portname" or "comportname")) continue;
            if (key.GetValue(name) is string value &&
                System.Text.RegularExpressions.Regex.IsMatch(value, @"\ACOM[1-9][0-9]{0,3}\z", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                result.Add(new ReservedSerialPort(value.ToUpperInvariant(), owner));
        }
        foreach (var child in key.GetSubKeyNames())
        {
            using var nested = key.OpenSubKey(child, writable: false);
            if (nested is not null) ReadReservations(nested, owner + "/" + child, result, depth + 1);
        }
    }

    public static UvexSerialPortCandidate RequireCandidate(UvexSafetyOptions options,
        IEnumerable<UvexSerialPortCandidate> present, IEnumerable<ReservedSerialPort> reserved)
    {
        options.ValidatePortName();
        reserved = reserved.Concat((options.ReservedSerialPorts ?? [])
            .Select(port => new ReservedSerialPort(port, "site-reserved equipment")));
        var conflict = reserved.FirstOrDefault(value => value.PortName.Equals(options.PortName, StringComparison.OrdinalIgnoreCase));
        if (conflict is not null)
            throw new InvalidOperationException($"UVEX_SERIAL_PORT_RESERVED: {options.PortName} is configured for {conflict.Owner}; it will not be opened or probed by UVEX. Rebind UVEX at an idle boundary.");
        var candidates = present.Where(value => value.PortName.Equals(options.PortName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (candidates.Length != 1)
            throw new InvalidOperationException($"UVEX_SERIAL_PORT_NOT_PRESENT: {options.PortName} has no unique present VID_{options.ExpectedUsbVid}/PID_{options.ExpectedUsbPid} device. Use explicit read-only identification after a USB change.");
        if (string.IsNullOrWhiteSpace(options.ExpectedUsbInstanceId))
            throw new InvalidOperationException($"UVEX_SERIAL_BINDING_REQUIRED: {options.PortName} needs an explicitly identified USB instance; CH340 VID/PID alone is not UVEX identity.");
        if (!candidates[0].InstanceId.Equals(options.ExpectedUsbInstanceId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"UVEX_SERIAL_BINDING_CHANGED: {options.PortName} no longer matches the saved USB instance; no automatic port scan or control is authorized.");
        return candidates[0];
    }
}
