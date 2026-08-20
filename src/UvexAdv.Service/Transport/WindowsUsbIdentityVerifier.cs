using Microsoft.Win32;
using System.Security;

namespace UvexAdv.Service.Transport;

internal static class WindowsUsbIdentityVerifier
{
    public static bool MatchesPort(string portName, string expectedVid, string expectedPid)
    {
        var vidMarker = $"VID_{expectedVid}";
        var pidMarker = $"PID_{expectedPid}";
        try
        {
            using var usb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB", writable: false);
            if (usb is null)
            {
                return false;
            }

            foreach (var deviceKeyName in TryGetSubKeyNames(usb).Where(name =>
                         name.Contains(vidMarker, StringComparison.OrdinalIgnoreCase) &&
                         name.Contains(pidMarker, StringComparison.OrdinalIgnoreCase)))
            {
                using var device = TryOpenSubKey(usb, deviceKeyName);
                if (device is null)
                {
                    continue;
                }

                foreach (var instanceName in TryGetSubKeyNames(device))
                {
                    using var instance = TryOpenSubKey(device, instanceName);
                    using var parameters = instance is null ? null : TryOpenSubKey(instance, "Device Parameters");
                    if (parameters?.GetValue("PortName") is string candidate &&
                        candidate.Equals(portName, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (IOException)
        {
        }
        catch (SecurityException)
        {
        }

        return false;
    }

    private static RegistryKey? TryOpenSubKey(RegistryKey parent, string name)
    {
        try
        {
            return parent.OpenSubKey(name, writable: false);
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (SecurityException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string[] TryGetSubKeyNames(RegistryKey key)
    {
        try
        {
            return key.GetSubKeyNames();
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
        catch (SecurityException)
        {
            return [];
        }
        catch (IOException)
        {
            return [];
        }
    }
}
