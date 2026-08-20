using System.Text.Json.Serialization;

namespace UvexAdv.Core;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DeviceConnectionState
{
    Disconnected,
    Connecting,
    Initializing,
    Ready,
    Busy,
    Faulted,
    Maintenance,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UvexPositionTrust
{
    Unknown,
    LastKnown,
    Live,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UvexOutputState
{
    Unknown,
    Off,
    On,
}

public sealed record UvexSlitDefinition(int Position, string Name, int? OffsetSteps)
{
    public string DisplayName => $"{Position} - {Name}";
}

[Flags]
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UvexCapabilities
{
    None = 0,
    MotorizedGrating = 1 << 0,
    MotorizedSlit = 1 << 1,
    MotorizedFocus = 1 << 2,
    Ethernet = 1 << 3,
    Wifi = 1 << 4,
    SlitPhotodiode = 1 << 5,
    Calibration = 1 << 6,
    FocusHallSensor = 1 << 7,
}

public sealed record UvexDeviceStatus
{
    public DeviceConnectionState ConnectionState { get; init; } = DeviceConnectionState.Disconnected;
    public string PortName { get; init; } = "COM5";
    public string? FirmwareVersion { get; init; }
    public string? Description { get; init; }
    public UvexCapabilities Capabilities { get; init; }
    public int? GratingPositionSteps { get; init; }
    public double? CentralWavelengthAngstrom { get; init; }
    public double? MinimumWavelengthAngstrom { get; init; }
    public double? MaximumWavelengthAngstrom { get; init; }
    public int? SlitPosition { get; init; }
    public int? SlitMotorPositionSteps { get; init; }
    public IReadOnlyList<UvexSlitDefinition> Slits { get; init; } = [];
    public int? SlitPhotodiodeValue { get; init; }
    public int? SlitPhotodiodeThreshold { get; init; }
    public bool? SlitPhotodiodeEnabled { get; init; }
    /// <summary>
    /// Last state successfully commanded by this service for the slit-wheel
    /// positioning LED. The UVEX serial protocol has no state query, so this is
    /// Unknown after a connection or controller fault until SLON/SLOF succeeds.
    /// </summary>
    public UvexOutputState SlitIlluminationLedState { get; init; } = UvexOutputState.Unknown;
    public DateTimeOffset? SlitIlluminationLedCommandedUtc { get; init; }
    public int? FocusPositionSteps { get; init; }
    public double? TemperatureC { get; init; }
    public bool PositionKnown { get; init; }
    public UvexPositionTrust PositionTrust { get; init; }
    public DateTimeOffset? PositionMeasuredUtc { get; init; }
    public string? LastError { get; init; }
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class UvexSafetyOptions
{
    public string PortName { get; set; } = "COM5";
    public string ExpectedUsbVid { get; set; } = "1A86";
    public string ExpectedUsbPid { get; set; } = "7523";
    public int ExpectedGratingLinesPerMm { get; set; } = 300;
    public bool HardwareIdentityVerified { get; set; }
    public bool Simulator { get; set; } = true;
    public int GratingMinimumSteps { get; set; } = -250_000;
    public int GratingMaximumSteps { get; set; } = 250_000;
    public int GratingMaximumSingleMoveSteps { get; set; } = 20_000;
    public int FocusMinimumSteps { get; set; } = -20_000;
    public int FocusMaximumSteps { get; set; } = 20_000;
    public int FocusMaximumSingleMoveSteps { get; set; } = 2_000;
    public int SlitPositions { get; set; } = 4;
    public string[] SlitNames { get; set; } = ["300um", "15um", "25um", "35um"];
    public int SlitOffsetMaximumAbsoluteSteps { get; set; } = 2000;
    public bool UseSlitPhotodiode { get; set; } = true;
    public TimeSpan SerialOpenDelay { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan SerialPostMotionSettleDelay { get; set; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(3);
    public TimeSpan MotionTimeout { get; set; } = TimeSpan.FromSeconds(45);

    public void ValidateForMotion()
    {
        if (!Simulator && !HardwareIdentityVerified)
        {
            throw new InvalidOperationException(
                $"Motion is blocked until {PortName} identity matches VID_{ExpectedUsbVid}&PID_{ExpectedUsbPid} and HardwareIdentityVerified is enabled.");
        }

        if (!PortName.Equals("COM5", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("This installation is safety-locked to COM5. Edit and revalidate the deployment configuration to change it.");
        }
    }
}
