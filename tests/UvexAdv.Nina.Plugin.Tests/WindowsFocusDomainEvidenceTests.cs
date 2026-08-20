using UvexAdv.Core;
using UvexAdv.Observatory;
using UvexAdv.Phd2;
using UvexAdv.Qhy.Core;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class WindowsFocusDomainEvidenceTests
{
    private const string C11Instance = @"USB\VID_1A86&PID_7523\8&103F253C&0&1";
    private const string UvexInstance = @"USB\VID_1A86&PID_7523\8&103F253C&0&2";
    private const string Gs350Instance = @"USB\VID_0547&PID_14AD\6&21FC78A&0&1";
    private const string Gs350Topology = @"PCIROOT(0)#PCI(0803)#PCI(0003)#USBROOT(0)#USB(1)";

    [Fact]
    public void ResolvesMachineStyleComBindingsAndExactGs350Topology()
    {
        var metric = CreateQhyMetric(DateTimeOffset.UtcNow);
        var reader = CreateReader(
            Device(C11Instance, "COM8", "Port_#0001.Hub_#0009"),
            Device(UvexInstance, "COM5", "Port_#0002.Hub_#0009"),
            Device(Gs350Instance, null, Gs350Topology, "AUTOFOCUSER"));

        var snapshot = reader.BuildLiveFocusDomains(new WindowsFocusDomainEvidenceInput(
            C11Connected: true,
            C11LogicalDeviceId: FocusDomainConventions.C11LogicalDeviceId,
            C11PositionSteps: 4758,
            Gs350Owner: "ManualOperator",
            Gs350LogicalDeviceId: FocusDomainConventions.Gs350LogicalDeviceId,
            Gs350PositionSteps: null,
            CurrentQhyMetric: metric,
            UvexM2PositionSteps: 12000));

        Assert.Equal(3, snapshot.FocusDomains.Count);
        Assert.All(snapshot.EvidenceGates, gate => Assert.Equal(GateDisposition.Passed, gate.Disposition));

        var c11 = snapshot.FocusDomains.Single(state => state.Role == FocusDomainRole.C11Main);
        Assert.Equal(C11Instance, c11.PhysicalBinding.HardwareInstanceId);
        Assert.Equal("COM8", c11.PhysicalBinding.ConnectionEndpoint);
        Assert.Equal(4758, c11.PositionSteps);

        var gs350 = snapshot.FocusDomains.Single(state => state.Role == FocusDomainRole.Gs350WideField);
        Assert.Equal(Gs350Instance, gs350.PhysicalBinding.HardwareInstanceId);
        Assert.Equal(Gs350Topology, gs350.PhysicalBinding.TopologyPath);
        Assert.Null(gs350.PositionSteps);
        Assert.Same(metric, gs350.Metric);

        var uvex = snapshot.FocusDomains.Single(state => state.Role == FocusDomainRole.UvexSpectral);
        Assert.Equal(UvexInstance, uvex.PhysicalBinding.HardwareInstanceId);
        Assert.Equal("COM5", uvex.PhysicalBinding.ConnectionEndpoint);
        Assert.Equal(12000, uvex.PositionSteps);
    }

    [Fact]
    public void AmbiguousGs350InstancesRemainUnknownAndAreNotCopiedFromLock()
    {
        var reader = CreateReader(
            Device(C11Instance, "COM8", null),
            Device(UvexInstance, "COM5", null),
            Device(Gs350Instance, null, Gs350Topology),
            Device(@"USB\VID_0547&PID_14AD\7&AAAA&0&2", null, @"PCIROOT(0)#USBROOT(0)#USB(2)"));

        var snapshot = reader.BuildLiveFocusDomains(DefaultInput());

        Assert.DoesNotContain(snapshot.FocusDomains, state => state.Role == FocusDomainRole.Gs350WideField);
        var gate = Assert.Single(snapshot.EvidenceGates, candidate => candidate.Code == "FOCUS_GS350_WIDE_FIELD_WINDOWS_PNP");
        Assert.Equal(GateDisposition.Indeterminate, gate.Disposition);
        Assert.Contains("ambiguous", gate.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingDevicesRemainUnknownRatherThanUsingExpectedIdentities()
    {
        var snapshot = CreateReader().BuildLiveFocusDomains(DefaultInput());

        Assert.Empty(snapshot.FocusDomains);
        Assert.Equal(3, snapshot.EvidenceGates.Count);
        Assert.All(snapshot.EvidenceGates, gate => Assert.Equal(GateDisposition.Indeterminate, gate.Disposition));
    }

    [Fact]
    public void CorrectVidPidOnWrongComEndpointDoesNotSatisfyC11Binding()
    {
        var reader = CreateReader(
            Device(C11Instance, "COM7", null),
            Device(@"USB\VID_9999&PID_0001\WRONG-COM8", "COM8", null),
            Device(UvexInstance, "COM5", null),
            Device(Gs350Instance, null, Gs350Topology));

        var snapshot = reader.BuildLiveFocusDomains(DefaultInput());

        Assert.DoesNotContain(snapshot.FocusDomains, state => state.Role == FocusDomainRole.C11Main);
        var gate = Assert.Single(snapshot.EvidenceGates, candidate => candidate.Code == "FOCUS_C11_MAIN_WINDOWS_PNP");
        Assert.Equal(GateDisposition.Indeterminate, gate.Disposition);
        Assert.Contains("COM8", gate.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Gs350WithoutTopologyRemainsUnknown()
    {
        var reader = CreateReader(
            Device(C11Instance, "COM8", null),
            Device(UvexInstance, "COM5", null),
            Device(Gs350Instance, null, null));

        var snapshot = reader.BuildLiveFocusDomains(DefaultInput());

        Assert.DoesNotContain(snapshot.FocusDomains, state => state.Role == FocusDomainRole.Gs350WideField);
        var gate = Assert.Single(snapshot.EvidenceGates, candidate => candidate.Code == "FOCUS_GS350_WIDE_FIELD_WINDOWS_PNP");
        Assert.Equal(GateDisposition.Indeterminate, gate.Disposition);
        Assert.Contains("topology", gate.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EvaluateLivePassesSuppliedFocusStatesAndOldCallCannotPassSchema2()
    {
        var now = DateTimeOffset.Parse("2026-08-17T02:00:00+08:00");
        var setup = CreateNightSetup(now);
        var snapshot = new LoadedNightSetupSnapshot(setup, @"C:\evidence\night-setup.json", new string('A', 64));
        var reader = CreateReader(
            Device(C11Instance, "COM8", null),
            Device(UvexInstance, "COM5", null),
            Device(Gs350Instance, null, Gs350Topology));
        var focusEvidence = reader.BuildLiveFocusDomains(new WindowsFocusDomainEvidenceInput(
            true,
            FocusDomainConventions.C11LogicalDeviceId,
            4758,
            "ManualOperator",
            FocusDomainConventions.Gs350LogicalDeviceId,
            18000,
            null,
            12000));

        var gates = LockedNightSetupSnapshotLoader.EvaluateLive(
            snapshot,
            new AtrLiveInfo("atr", 100, 256, 1, 1, -10, "1", false, 100, 100),
            new UvexDeviceStatus
            {
                SlitPosition = 2,
                GratingPositionSteps = 100,
                FocusPositionSteps = 12000,
            },
            new Phd2ProfileBindingSnapshot(
                1, "profile", "ToupTek Camera", ["g3"], "OnStep Telescope (ASCOM)",
                1, 100, 2150, 16, "registry", new string('B', 64), now),
            new QhyCameraStatus(
                true,
                new QhyCameraIdentity("qhy", "QHYminiCam8M", "native"),
                -10,
                20,
                null,
                now),
            focusEvidence.FocusDomains,
            now);

        Assert.Equal(GateDisposition.Passed, Gate(gates, "FOCUS_C11_MAIN_IDENTITY").Disposition);
        Assert.Equal(GateDisposition.Passed, Gate(gates, "FOCUS_C11_MAIN_POSITION").Disposition);
        Assert.Equal(GateDisposition.Passed, Gate(gates, "FOCUS_GS350_WIDE_FIELD_TOPOLOGY").Disposition);
        Assert.Equal(GateDisposition.Passed, Gate(gates, "FOCUS_UVEX_SPECTRAL_POSITION").Disposition);

        var legacyCallShape = LockedNightSetupSnapshotLoader.EvaluateLive(
            snapshot,
            new AtrLiveInfo("atr", 100, 256, 1, 1, -10, "1", false, 100, 100),
            new UvexDeviceStatus
            {
                SlitPosition = 2,
                GratingPositionSteps = 100,
                FocusPositionSteps = 12000,
            },
            new Phd2ProfileBindingSnapshot(
                1, "profile", "ToupTek Camera", ["g3"], "OnStep Telescope (ASCOM)",
                1, 100, 2150, 16, "registry", new string('B', 64), now),
            new QhyCameraStatus(
                true,
                new QhyCameraIdentity("qhy", "QHYminiCam8M", "native"),
                -10,
                20,
                null,
                now));

        Assert.Equal(
            GateDisposition.Indeterminate,
            Gate(legacyCallShape, "FOCUS_C11_MAIN_LIVE_STATE").Disposition);
        Assert.Equal(
            GateDisposition.Indeterminate,
            Gate(legacyCallShape, "FOCUS_GS350_WIDE_FIELD_LIVE_STATE").Disposition);
        Assert.Equal(
            GateDisposition.Indeterminate,
            Gate(legacyCallShape, "FOCUS_UVEX_SPECTRAL_LIVE_STATE").Disposition);
    }

    private static GateResult Gate(IReadOnlyList<GateResult> gates, string code) =>
        Assert.Single(gates, gate => gate.Code == code);

    private static WindowsFocusDomainEvidenceInput DefaultInput() => new(
        true,
        FocusDomainConventions.C11LogicalDeviceId,
        4758,
        "ManualOperator",
        FocusDomainConventions.Gs350LogicalDeviceId,
        null,
        null,
        12000);

    private static WindowsFocusDomainEvidence CreateReader(params WindowsPnpFocusDevice[] devices) =>
        new(new StubProbe(devices));

    private static WindowsPnpFocusDevice Device(
        string instanceId,
        string? portName,
        string? topologyPath,
        string? friendlyName = null) =>
        new(instanceId, portName, topologyPath, friendlyName);

    private static LiveFocusMetricState CreateQhyMetric(DateTimeOffset now) => new(
        new FocusMetricEvidence(
            FocusMetricKind.QhyStellarShapeAndPlateSolve,
            "qhy",
            2.1,
            "FWHM pixels",
            new string('4', 64)),
        now.AddSeconds(-5),
        now.AddMinutes(1),
        GateDisposition.Passed);

    private static NightSetupRecord CreateNightSetup(DateTimeOffset now)
    {
        var atr = new CameraSetup("atr", 100, 256, 1, 1, -10, "1", 0, 0, 100, 100);
        var qhy = new CameraSetup("qhy", 10, 256, 1, 1, -10, "1", 0, 0, 100, 100);
        var lockedUtc = now.AddMinutes(-10);
        return new NightSetupRecord(
            NightSetupRecord.CurrentSchemaVersion,
            "setup",
            lockedUtc,
            2,
            15,
            100,
            550,
            12000,
            4758,
            atr,
            "g3",
            "profile",
            qhy,
            DispersionDirection.BlueAtLeftRedAtRight,
            400,
            700,
            CalibrationStrategy.BrightReferenceStar,
            "reference",
            new HorizonPolicy(),
            "supervised",
            false,
            6800,
            [
                new FocusDomainBinding(
                    FocusDomainRole.C11Main,
                    FocusDomainConventions.C11Owner,
                    FocusDomainConventions.C11LogicalDeviceId,
                    new FocusPhysicalBinding(FocusMechanism.Gemini, "COM8", C11Instance, null),
                    4758,
                    new FocusMotionLimits(0, 100000, 200, 1000, FocusApproachDirection.IncreasingSteps, 50),
                    new FocusMetricEvidence(FocusMetricKind.G3StellarShape, "g3", 2.3, "FWHM pixels", new string('1', 64)),
                    lockedUtc,
                    now.AddHours(1),
                    0.95),
                new FocusDomainBinding(
                    FocusDomainRole.Gs350WideField,
                    "ManualOperator",
                    FocusDomainConventions.Gs350LogicalDeviceId,
                    new FocusPhysicalBinding(FocusMechanism.ToupTekAaf, "AUTOFOCUSER", Gs350Instance, Gs350Topology),
                    18000,
                    new FocusMotionLimits(0, 50000, 0, 0, FocusApproachDirection.None, 0),
                    new FocusMetricEvidence(FocusMetricKind.QhyStellarShapeAndPlateSolve, "qhy", 2.1, "FWHM pixels", new string('2', 64)),
                    lockedUtc,
                    now.AddHours(1),
                    0.93),
                new FocusDomainBinding(
                    FocusDomainRole.UvexSpectral,
                    FocusDomainConventions.UvexOwner,
                    FocusDomainConventions.UvexLogicalDeviceId,
                    new FocusPhysicalBinding(FocusMechanism.UvexM2, "COM5", UvexInstance, null),
                    12000,
                    new FocusMotionLimits(-50000, 50000, 100, 500, FocusApproachDirection.IncreasingSteps, 25),
                    new FocusMetricEvidence(FocusMetricKind.AtrSpectralLineWidth, "atr", 2.7, "FWHM pixels", new string('3', 64)),
                    lockedUtc,
                    now.AddHours(1),
                    0.97),
            ]);
    }

    private sealed record AtrLiveInfo(
        string DeviceId,
        int Gain,
        int Offset,
        int BinX,
        int BinY,
        double Temperature,
        string ReadoutMode,
        bool IsSubSampleEnabled,
        int XSize,
        int YSize);

    private sealed class StubProbe(IReadOnlyList<WindowsPnpFocusDevice> devices) : IWindowsFocusDeviceProbe
    {
        public IReadOnlyList<WindowsPnpFocusDevice> ReadPresentUsbDevices() => devices;
    }
}
