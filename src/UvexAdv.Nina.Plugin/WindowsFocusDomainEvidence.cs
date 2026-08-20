using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;
using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

/// <summary>
/// Runtime facts supplied by the current device owners. None of these values
/// are read from the locked Night Setup: the lock is the expected side of the
/// later comparison, never a substitute for live evidence.
/// </summary>
public sealed record WindowsFocusDomainEvidenceInput(
    bool C11Connected,
    string? C11LogicalDeviceId,
    int? C11PositionSteps,
    string? Gs350Owner,
    string? Gs350LogicalDeviceId,
    int? Gs350PositionSteps,
    LiveFocusMetricState? CurrentQhyMetric,
    int? UvexM2PositionSteps);

/// <summary>
/// A fail-closed focus identity snapshot. A missing or ambiguous PnP role is
/// omitted from <see cref="FocusDomains"/> so Night Setup compatibility reports
/// an indeterminate live-state gate rather than accepting configured values as
/// measurements.
/// </summary>
public sealed record WindowsFocusDomainEvidenceSnapshot(
    IReadOnlyList<LiveFocusDomainState> FocusDomains,
    IReadOnlyList<GateResult> EvidenceGates);

/// <summary>
/// Resolves the three focus mechanisms from the current Windows PnP device set.
/// This class is read-only: it does not instantiate an ASCOM driver, open a COM
/// port, enumerate a camera SDK, or move a focuser.
/// </summary>
public sealed class WindowsFocusDomainEvidence
{
    private const string Ch340VidPid = "VID_1A86&PID_7523";
    private const string ToupTekAafVidPid = "VID_0547&PID_14AD";
    private readonly IWindowsFocusDeviceProbe probe;

    public WindowsFocusDomainEvidence()
        : this(new WindowsRegistryPnpFocusDeviceProbe())
    {
    }

    internal WindowsFocusDomainEvidence(IWindowsFocusDeviceProbe probe)
    {
        this.probe = probe ?? throw new ArgumentNullException(nameof(probe));
    }

    public WindowsFocusDomainEvidenceSnapshot BuildLiveFocusDomains(
        WindowsFocusDomainEvidenceInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        IReadOnlyList<WindowsPnpFocusDevice> devices;
        try
        {
            devices = probe.ReadPresentUsbDevices();
        }
        catch (Exception ex)
        {
            var reason = $"Windows PnP focus identity could not be read: {ex.Message}";
            return new WindowsFocusDomainEvidenceSnapshot(
                [],
                Enum.GetValues<FocusDomainRole>()
                    .Select(role => GateResult.Unknown(PnpGateCode(role), reason))
                    .ToArray());
        }

        var states = new List<LiveFocusDomainState>(3);
        var gates = new List<GateResult>(3);

        AddC11State(input, devices, states, gates);
        AddGs350State(input, devices, states, gates);
        AddUvexState(input, devices, states, gates);

        return new WindowsFocusDomainEvidenceSnapshot(states.AsReadOnly(), gates.AsReadOnly());
    }

    private static void AddC11State(
        WindowsFocusDomainEvidenceInput input,
        IReadOnlyList<WindowsPnpFocusDevice> devices,
        List<LiveFocusDomainState> states,
        List<GateResult> gates)
    {
        const FocusDomainRole role = FocusDomainRole.C11Main;
        if (!input.C11Connected)
        {
            gates.Add(GateResult.Unknown(PnpGateCode(role),
                "N.I.N.A. does not report the C11 Star Focuser Pro owner as connected; a present USB adapter alone is not live focuser evidence."));
            return;
        }
        if (string.IsNullOrWhiteSpace(input.C11LogicalDeviceId))
        {
            gates.Add(GateResult.Unknown(PnpGateCode(role),
                "N.I.N.A. reports a connected C11 focuser but did not expose its current logical device ID."));
            return;
        }

        var resolution = ResolveComDevice(devices, FocusDomainConventions.C11ConnectionEndpoint, Ch340VidPid);
        gates.Add(resolution.Gate with { Code = PnpGateCode(role) });
        if (resolution.Device is null) return;

        states.Add(new LiveFocusDomainState(
            role,
            FocusDomainConventions.C11Owner,
            input.C11LogicalDeviceId.Trim(),
            new FocusPhysicalBinding(
                FocusMechanism.Gemini,
                FocusDomainConventions.C11ConnectionEndpoint,
                resolution.Device.InstanceId,
                null),
            input.C11PositionSteps));
    }

    private static void AddGs350State(
        WindowsFocusDomainEvidenceInput input,
        IReadOnlyList<WindowsPnpFocusDevice> devices,
        List<LiveFocusDomainState> states,
        List<GateResult> gates)
    {
        const FocusDomainRole role = FocusDomainRole.Gs350WideField;
        var candidates = devices
            .Where(device => ContainsVidPid(device.InstanceId, ToupTekAafVidPid))
            .ToArray();
        if (candidates.Length != 1)
        {
            gates.Add(GateResult.Unknown(PnpGateCode(role), candidates.Length == 0
                ? $"No current present Windows PnP instance for {ToupTekAafVidPid} was found."
                : $"Windows reports {candidates.Length} current present {ToupTekAafVidPid} instances; the GS350 AAF identity is ambiguous."));
            return;
        }

        var device = candidates[0];
        if (string.IsNullOrWhiteSpace(device.TopologyPath))
        {
            gates.Add(GateResult.Unknown(PnpGateCode(role),
                $"Windows found '{device.InstanceId}' but did not expose a PnP location path; the GS350 USB topology cannot be attested."));
            return;
        }
        if (string.IsNullOrWhiteSpace(input.Gs350Owner) || string.IsNullOrWhiteSpace(input.Gs350LogicalDeviceId))
        {
            gates.Add(GateResult.Unknown(PnpGateCode(role),
                "The GS350 AAF is present, but its current runtime owner/logical identity was not supplied; the locked Night Setup is not copied into live evidence."));
            return;
        }

        gates.Add(GateResult.Pass(PnpGateCode(role),
            $"Resolved the single present GS350 AAF instance '{device.InstanceId}' at topology '{device.TopologyPath}'."));
        states.Add(new LiveFocusDomainState(
            role,
            input.Gs350Owner.Trim(),
            input.Gs350LogicalDeviceId.Trim(),
            new FocusPhysicalBinding(
                FocusMechanism.ToupTekAaf,
                FocusDomainConventions.Gs350ConnectionEndpoint,
                device.InstanceId,
                device.TopologyPath.Trim()),
            input.Gs350PositionSteps,
            input.CurrentQhyMetric));
    }

    private static void AddUvexState(
        WindowsFocusDomainEvidenceInput input,
        IReadOnlyList<WindowsPnpFocusDevice> devices,
        List<LiveFocusDomainState> states,
        List<GateResult> gates)
    {
        const FocusDomainRole role = FocusDomainRole.UvexSpectral;
        var resolution = ResolveComDevice(devices, FocusDomainConventions.UvexConnectionEndpoint, Ch340VidPid);
        gates.Add(resolution.Gate with { Code = PnpGateCode(role) });
        if (resolution.Device is null) return;

        states.Add(new LiveFocusDomainState(
            role,
            FocusDomainConventions.UvexOwner,
            FocusDomainConventions.UvexLogicalDeviceId,
            new FocusPhysicalBinding(
                FocusMechanism.UvexM2,
                FocusDomainConventions.UvexConnectionEndpoint,
                resolution.Device.InstanceId,
                null),
            input.UvexM2PositionSteps));
    }

    private static DeviceResolution ResolveComDevice(
        IReadOnlyList<WindowsPnpFocusDevice> devices,
        string endpoint,
        string requiredVidPid)
    {
        var candidates = devices
            .Where(device => string.Equals(device.PortName?.Trim(), endpoint, StringComparison.OrdinalIgnoreCase))
            .Where(device => ContainsVidPid(device.InstanceId, requiredVidPid))
            .ToArray();
        if (candidates.Length == 1)
        {
            return new DeviceResolution(
                candidates[0],
                GateResult.Pass("WINDOWS_PNP", $"Resolved {endpoint} to current present PnP instance '{candidates[0].InstanceId}'."));
        }

        return new DeviceResolution(
            null,
            GateResult.Unknown("WINDOWS_PNP", candidates.Length == 0
                ? $"No current present {requiredVidPid} PnP instance is registered as {endpoint}."
                : $"Windows reports {candidates.Length} current present {requiredVidPid} instances registered as {endpoint}; identity is ambiguous."));
    }

    private static bool ContainsVidPid(string instanceId, string vidPid) =>
        NormalizeInstanceId(instanceId).Contains(vidPid, StringComparison.Ordinal);

    private static string NormalizeInstanceId(string value) =>
        (value ?? string.Empty).Replace('/', '\\').Trim().ToUpperInvariant();

    private static string PnpGateCode(FocusDomainRole role) =>
        $"FOCUS_{FocusDomainConventions.Code(role)}_WINDOWS_PNP";

    private sealed record DeviceResolution(WindowsPnpFocusDevice? Device, GateResult Gate);
}

internal sealed record WindowsPnpFocusDevice(
    string InstanceId,
    string? PortName,
    string? TopologyPath,
    string? FriendlyName);

internal interface IWindowsFocusDeviceProbe
{
    /// <summary>
    /// Returns only devices currently enumerated with DIGCF_PRESENT. Historical
    /// registry entries must never be returned as live identity evidence.
    /// </summary>
    IReadOnlyList<WindowsPnpFocusDevice> ReadPresentUsbDevices();
}

/// <summary>
/// Uses SetupAPI to enumerate only present PnP instances, then reads PortName
/// from that exact instance's registry node. LocationPaths comes from the PnP
/// property store, with LocationInformation used only as a diagnostic fallback.
/// </summary>
internal sealed class WindowsRegistryPnpFocusDeviceProbe : IWindowsFocusDeviceProbe
{
    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfAllClasses = 0x00000004;
    private static readonly IntPtr InvalidHandleValue = new(-1);
    private static readonly DevPropKey DeviceLocationPaths = new(
        new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
        37);

    public IReadOnlyList<WindowsPnpFocusDevice> ReadPresentUsbDevices()
    {
        if (!OperatingSystem.IsWindows()) return [];

        var deviceInfoSet = SetupDiGetClassDevsW(
            IntPtr.Zero,
            null,
            IntPtr.Zero,
            DigcfPresent | DigcfAllClasses);
        if (deviceInfoSet == InvalidHandleValue)
        {
            throw new InvalidOperationException($"SetupDiGetClassDevs failed with Win32 error {Marshal.GetLastWin32Error()}.");
        }

        try
        {
            var devices = new List<WindowsPnpFocusDevice>();
            for (uint index = 0; ; index++)
            {
                var deviceInfo = new SpDevInfoData { Size = (uint)Marshal.SizeOf<SpDevInfoData>() };
                if (!SetupDiEnumDeviceInfo(deviceInfoSet, index, ref deviceInfo))
                {
                    const int noMoreItems = 259;
                    var error = Marshal.GetLastWin32Error();
                    if (error == noMoreItems) break;
                    throw new InvalidOperationException($"SetupDiEnumDeviceInfo failed with Win32 error {error}.");
                }

                var instanceId = ReadInstanceId(deviceInfoSet, ref deviceInfo);
                if (!instanceId.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase)) continue;
                if (!instanceId.Contains("VID_", StringComparison.OrdinalIgnoreCase) ||
                    !instanceId.Contains("&PID_", StringComparison.OrdinalIgnoreCase)) continue;

                var values = ReadRegistryValues(instanceId);
                var topologyPath = ReadLocationPath(deviceInfoSet, ref deviceInfo)
                    ?? values.LocationInformation;
                devices.Add(new WindowsPnpFocusDevice(
                    instanceId,
                    values.PortName,
                    topologyPath,
                    values.FriendlyName));
            }

            return devices.AsReadOnly();
        }
        finally
        {
            _ = SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }
    }

    private static string ReadInstanceId(IntPtr deviceInfoSet, ref SpDevInfoData deviceInfo)
    {
        var buffer = new StringBuilder(1024);
        if (!SetupDiGetDeviceInstanceIdW(deviceInfoSet, ref deviceInfo, buffer, buffer.Capacity, out _))
        {
            throw new InvalidOperationException($"SetupDiGetDeviceInstanceId failed with Win32 error {Marshal.GetLastWin32Error()}.");
        }
        return buffer.ToString();
    }

    private static string? ReadLocationPath(IntPtr deviceInfoSet, ref SpDevInfoData deviceInfo)
    {
        var buffer = new byte[8192];
        var locationPaths = DeviceLocationPaths;
        if (!SetupDiGetDevicePropertyW(
                deviceInfoSet,
                ref deviceInfo,
                ref locationPaths,
                out _,
                buffer,
                (uint)buffer.Length,
                out var requiredSize,
                0))
        {
            return null;
        }

        var byteCount = (int)Math.Min(requiredSize, (uint)buffer.Length);
        byteCount -= byteCount % sizeof(char);
        if (byteCount <= 0) return null;
        return Encoding.Unicode.GetString(buffer, 0, byteCount)
            .Split('\0', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
    }

    [SupportedOSPlatform("windows")]
    private static RegistryValues ReadRegistryValues(string instanceId)
    {
        try
        {
            using var instance = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Enum\" + instanceId,
                writable: false);
            using var parameters = instance?.OpenSubKey("Device Parameters", writable: false);
            return new RegistryValues(
                parameters?.GetValue("PortName")?.ToString(),
                instance?.GetValue("LocationInformation")?.ToString(),
                instance?.GetValue("FriendlyName")?.ToString());
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return new RegistryValues(null, null, null);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevInfoData
    {
        public uint Size;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey(Guid formatId, uint propertyId)
    {
        public Guid FormatId = formatId;
        public uint PropertyId = propertyId;
    }

    private sealed record RegistryValues(
        string? PortName,
        string? LocationInformation,
        string? FriendlyName);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(
        IntPtr classGuid,
        string? enumerator,
        IntPtr parentWindow,
        uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(
        IntPtr deviceInfoSet,
        uint memberIndex,
        ref SpDevInfoData deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInstanceIdW(
        IntPtr deviceInfoSet,
        ref SpDevInfoData deviceInfoData,
        StringBuilder deviceInstanceId,
        int deviceInstanceIdSize,
        out int requiredSize);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDevicePropertyW(
        IntPtr deviceInfoSet,
        ref SpDevInfoData deviceInfoData,
        ref DevPropKey propertyKey,
        out uint propertyType,
        [Out] byte[] propertyBuffer,
        uint propertyBufferSize,
        out uint requiredSize,
        uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);
}
