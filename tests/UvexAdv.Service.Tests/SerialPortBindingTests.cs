using UvexAdv.Core;
using UvexAdv.Service.Transport;

namespace UvexAdv.Service.Tests;

public sealed class SerialPortBindingTests
{
    private static UvexSafetyOptions Options(string port = "COM8") => new()
    {
        PortName = port, Simulator = false, ExpectedUsbInstanceId = @"USB\VID_1A86&PID_7523\test-device",
    };

    [Theory]
    [InlineData("COM3")]
    [InlineData("COM8")]
    [InlineData("com12")]
    public void ExplicitNonCom5BindingIsSupported(string port)
    {
        var options = Options(port);
        var candidate = new UvexSerialPortCandidate(port, options.ExpectedUsbInstanceId);
        Assert.Equal(candidate, WindowsSerialPortInventory.RequireCandidate(options, [candidate], []));
        options.HardwareIdentityVerified = true;
        options.ValidateForMotion();
    }

    [Theory]
    [InlineData("COM0")]
    [InlineData("COM05")]
    [InlineData("AUTO")]
    [InlineData("COM8,COM5")]
    [InlineData("\\\\.\\COM8")]
    [InlineData("COM8\n")]
    [InlineData("")]
    public void RejectsNonExplicitOrInvalidPort(string port) =>
        Assert.Throws<InvalidOperationException>(() => Options(port).ValidatePortName());

    [Theory]
    [InlineData("Dome Drivers/RRCI.Dome")]
    [InlineData("Telescope Drivers/ASCOM.OnStep")]
    [InlineData("CoverCalibrator Drivers/Cover")]
    [InlineData("Switch Drivers/Power")]
    public void OtherDeviceReservationWinsEvenWhenUsbIdMatches(string owner)
    {
        var options = Options("COM5");
        var error = Assert.Throws<InvalidOperationException>(() => WindowsSerialPortInventory.RequireCandidate(
            options, [new("COM5", options.ExpectedUsbInstanceId)], [new("com5", owner)]));
        Assert.Contains("UVEX_SERIAL_PORT_RESERVED", error.Message);
        Assert.Contains(owner, error.Message);
    }

    [Fact]
    public void RejectsMissingOrAmbiguousPresentDevice()
    {
        var options = Options();
        Assert.Throws<InvalidOperationException>(() => WindowsSerialPortInventory.RequireCandidate(options, [], []));
        Assert.Throws<InvalidOperationException>(() => WindowsSerialPortInventory.RequireCandidate(
            options, [new("COM8", options.ExpectedUsbInstanceId), new("COM8", "different-device")], []));
    }

    [Fact]
    public void GenericChipMatchDoesNotAuthorizeUnboundOrChangedDevice()
    {
        var options = Options();
        var candidate = new UvexSerialPortCandidate("COM8", options.ExpectedUsbInstanceId);
        options.ExpectedUsbInstanceId = "";
        Assert.Contains("BINDING_REQUIRED", Assert.Throws<InvalidOperationException>(() =>
            WindowsSerialPortInventory.RequireCandidate(options, [candidate], [])).Message);
        options.ExpectedUsbInstanceId = "old-instance";
        Assert.Contains("BINDING_CHANGED", Assert.Throws<InvalidOperationException>(() =>
            WindowsSerialPortInventory.RequireCandidate(options, [candidate], [])).Message);
    }

    [Fact]
    public void SiteReservationProtectsDriversWithoutAscomPortMetadata()
    {
        var options = Options();
        options.ReservedSerialPorts = ["COM8"];
        Assert.Contains("PORT_RESERVED", Assert.Throws<InvalidOperationException>(() =>
            WindowsSerialPortInventory.RequireCandidate(options, [new("COM8", options.ExpectedUsbInstanceId)], [])).Message);
    }

    [Fact]
    public void NonCom5MotionStillRequiresIdentity()
    {
        var options = Options();
        Assert.Throws<InvalidOperationException>(options.ValidateForMotion);
    }
}
