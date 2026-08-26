using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Observatory.Tests;

public sealed class NightSetupTests
{
    [Fact]
    public void SetupRejectsWrongPhysicalSlitWidthAndLegacyAtrOffset()
    {
        var setup = CreateSetup() with
        {
            SlitPosition = 4,
            SlitWidthMicrometers = 25,
            Atr585m = CreateSetup().Atr585m with { Offset = 16 }
        };

        var issues = setup.Validate();

        Assert.Contains(issues, issue => issue.Contains("35", StringComparison.Ordinal));
        Assert.Contains(issues, issue => issue.Contains("offset 256", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CompatibilityReturnsExplicitFailureInsteadOfChangingSetup()
    {
        var setup = CreateSetup();
        var live = new LiveSetupState(
            "wrong-atr", 100, 256, 1, 1, -10, "High Conversion Gain",
            2, -1923, 12000, "c11+ccdt67+slit+2210", "g3", "qhy");

        var gates = NightSetupCompatibility.Evaluate(setup, live);

        Assert.Contains(gates, gate => gate.Code == "ATR_IDENTITY" && gate.Disposition == GateDisposition.Failed);
        Assert.DoesNotContain(gates, gate => gate.Code == "ATR_OFFSET" && gate.Disposition != GateDisposition.Passed);
    }

    [Fact]
    public void SetupRejectsMechanismsAndMetricsAssignedToWrongOpticalRoles()
    {
        var setup = CreateSetup();
        var domains = setup.FocusDomains!;
        var c11 = domains.Single(binding => binding.Role == FocusDomainRole.C11Main);
        var spectral = domains.Single(binding => binding.Role == FocusDomainRole.UvexSpectral);
        setup = setup with
        {
            FocusDomains =
            [
                c11 with { Role = FocusDomainRole.UvexSpectral },
                domains.Single(binding => binding.Role == FocusDomainRole.Gs350WideField),
                spectral with { Role = FocusDomainRole.C11Main },
            ],
        };

        var issues = setup.Validate();

        Assert.Contains(issues, issue => issue.Contains("C11Main", StringComparison.Ordinal) && issue.Contains("Gemini", StringComparison.Ordinal));
        Assert.Contains(issues, issue => issue.Contains("UvexSpectral", StringComparison.Ordinal) && issue.Contains("UvexM2", StringComparison.Ordinal));
        Assert.Contains(issues, issue => issue.Contains("different optical path", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SetupRejectsMissingLogicalAndPhysicalIdentity()
    {
        var setup = CreateSetup();
        var domains = setup.FocusDomains!;
        var wide = domains.Single(binding => binding.Role == FocusDomainRole.Gs350WideField);
        setup = setup with
        {
            FocusDomains = domains.Select(binding => binding.Role == FocusDomainRole.Gs350WideField
                ? wide with
                {
                    LogicalDeviceId = string.Empty,
                    PhysicalBinding = wide.PhysicalBinding with { HardwareInstanceId = "USB\\VID_0547&PID_14AD", TopologyPath = null },
                }
                : binding).ToArray(),
        };

        var issues = setup.Validate();

        Assert.Contains(issues, issue => issue.Contains("logical device identity", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(issues, issue => issue.Contains("exact hardware instance", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(issues, issue => issue.Contains("topology", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CompatibilityFailsWhenGs350UsbTopologyChanges()
    {
        var setup = CreateSetup();
        var live = CreateLiveFocusStates(setup)
            .Select(state => state.Role == FocusDomainRole.Gs350WideField
                ? state with { PhysicalBinding = state.PhysicalBinding with { TopologyPath = "PCIROOT(0)#USBROOT(0)#USB(7)" } }
                : state)
            .ToArray();
        var liveSetup = CreateLiveSetup(live);

        var gates = NightSetupCompatibility.Evaluate(setup, liveSetup, evaluatedUtc: setup.LockedUtc.AddHours(1));

        Assert.Contains(gates, gate => gate.Code == "FOCUS_GS350_WIDE_FIELD_TOPOLOGY" && gate.Disposition == GateDisposition.Failed);
        Assert.DoesNotContain(gates, gate => gate.Code == "FOCUS_C11_MAIN_TOPOLOGY" && gate.Disposition != GateDisposition.Passed);
        Assert.DoesNotContain(gates, gate => gate.Code == "FOCUS_UVEX_SPECTRAL_TOPOLOGY" && gate.Disposition != GateDisposition.Passed);
    }

    [Fact]
    public void LegacyFocusDeadlineDoesNotExpireAnOtherwiseUnchangedState()
    {
        var setup = CreateSetup();
        var domains = setup.FocusDomains!;
        var wide = domains.Single(binding => binding.Role == FocusDomainRole.Gs350WideField);
        setup = setup with
        {
            FocusDomains = domains.Select(binding => binding.Role == FocusDomainRole.Gs350WideField
                ? wide with { ValidUntilUtc = setup.LockedUtc.AddMinutes(30) }
                : binding).ToArray(),
        };

        var gates = NightSetupCompatibility.Evaluate(
            setup,
            CreateLiveSetup(CreateLiveFocusStates(setup)),
            evaluatedUtc: setup.LockedUtc.AddHours(1));

        Assert.Contains(gates, gate => gate.Code == "FOCUS_GS350_WIDE_FIELD_EVIDENCE_STATE_BOUND" && gate.Disposition == GateDisposition.Passed);
        Assert.DoesNotContain(gates, gate => gate.Code.EndsWith("_EVIDENCE_FRESHNESS", StringComparison.Ordinal));
    }

    [Fact]
    public void Gs350ManualLockUsesCurrentQhyMetricWithoutClaimingALivePosition()
    {
        var setup = CreateSetup();
        var evaluatedUtc = setup.LockedUtc.AddHours(1);
        var currentMetric = new LiveFocusMetricState(
            new FocusMetricEvidence(
                FocusMetricKind.QhyStellarShapeAndPlateSolve,
                setup.QhyMiniCam8m.StableDeviceId,
                2.0,
                "FWHM pixels",
                new string('A', 64)),
            evaluatedUtc.AddMinutes(-1),
            evaluatedUtc.AddMinutes(2),
            GateDisposition.Passed);
        var liveDomains = CreateLiveFocusStates(setup)
            .Select(state => state.Role == FocusDomainRole.Gs350WideField
                ? state with { PositionSteps = null, Metric = currentMetric }
                : state)
            .ToArray();

        var gates = NightSetupCompatibility.Evaluate(setup, CreateLiveSetup(liveDomains), evaluatedUtc: evaluatedUtc);

        Assert.Contains(gates, gate => gate.Code == "FOCUS_GS350_WIDE_FIELD_LIVE_METRIC" && gate.Disposition == GateDisposition.Passed);
        Assert.Contains(gates, gate => gate.Code == "FOCUS_GS350_WIDE_FIELD_POSITION" && gate.Disposition == GateDisposition.Passed);

        var withoutCurrentMetric = liveDomains
            .Select(state => state.Role == FocusDomainRole.Gs350WideField ? state with { Metric = null } : state)
            .ToArray();
        var historicalOnlyGates = NightSetupCompatibility.Evaluate(setup, CreateLiveSetup(withoutCurrentMetric), evaluatedUtc: evaluatedUtc);
        Assert.Contains(historicalOnlyGates, gate => gate.Code == "FOCUS_GS350_WIDE_FIELD_LIVE_METRIC" && gate.Disposition == GateDisposition.Indeterminate);
        Assert.Contains(historicalOnlyGates, gate => gate.Code == "FOCUS_GS350_WIDE_FIELD_POSITION" && gate.Disposition == GateDisposition.Indeterminate);
    }

    [Theory]
    [InlineData(FocusDomainRole.C11Main, "FOCUS_C11_MAIN_POSITION")]
    [InlineData(FocusDomainRole.UvexSpectral, "FOCUS_UVEX_SPECTRAL_POSITION")]
    public void C11AndUvexDomainsStillRequireOwnerReportedPositions(FocusDomainRole role, string gateCode)
    {
        var setup = CreateSetup();
        var liveDomains = CreateLiveFocusStates(setup)
            .Select(state => state.Role == role ? state with { PositionSteps = null } : state)
            .ToArray();

        var gates = NightSetupCompatibility.Evaluate(
            setup,
            CreateLiveSetup(liveDomains),
            evaluatedUtc: setup.LockedUtc.AddHours(1));

        Assert.Contains(gates, gate => gate.Code == gateCode && gate.Disposition == GateDisposition.Indeterminate);
    }

    [Fact]
    public void LegacySchemaIsReadableButSingleTelescopeStepIsIndeterminateForRealCompatibility()
    {
        var legacy = CreateSetup() with
        {
            SchemaVersion = NightSetupRecord.LegacySchemaVersion,
            TelescopeFocusPositionSteps = 987654,
            FocusDomains = null,
        };

        var issues = legacy.Validate();
        var gates = NightSetupCompatibility.Evaluate(legacy, CreateLiveSetup(CreateLiveFocusStates(CreateSetup())));

        Assert.DoesNotContain(issues, issue => issue.Contains("schema version", StringComparison.OrdinalIgnoreCase));
        var legacyGate = Assert.Single(gates, gate => gate.Code == "FOCUS_DOMAINS_LEGACY_SCHEMA");
        Assert.Equal(GateDisposition.Indeterminate, legacyGate.Disposition);
        Assert.Contains("cannot attest", legacyGate.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(gates, gate => gate.Code.StartsWith("FOCUS_C11_MAIN_POSITION", StringComparison.Ordinal));
    }

    private static NightSetupRecord CreateSetup()
    {
        var atr = new CameraSetup("atr", 100, 256, 1, 1, -10, "High Conversion Gain", 0, 0, 3840, 2160);
        var qhy = new CameraSetup("qhy", 0, 0, 1, 1, null, "Linearity HDR", 0, 0, 3856, 2180);
        var lockedUtc = DateTimeOffset.UtcNow;
        var focusDomains = CreateFocusDomains(lockedUtc, atr.StableDeviceId, "g3", qhy.StableDeviceId);
        return new NightSetupRecord(
            NightSetupRecord.CurrentSchemaVersion, "setup", lockedUtc,
            2, 15, -1923, 559.15, 12000, 45000,
            atr, "g3", "c11+ccdt67+slit+2210", qhy,
            DispersionDirection.BlueAtLeftRedAtRight,
            382.8, 735.5,
            CalibrationStrategy.CompactEmissionLineObject,
            "NGC 6543",
            new HorizonPolicy(),
            "Operator run authorization",
            FocusDomains: focusDomains);
    }

    private static IReadOnlyList<FocusDomainBinding> CreateFocusDomains(
        DateTimeOffset lockedUtc,
        string atrId,
        string g3Id,
        string qhyId) =>
    [
        new(
            FocusDomainRole.C11Main,
            FocusDomainConventions.C11Owner,
            FocusDomainConventions.C11LogicalDeviceId,
            new FocusPhysicalBinding(FocusMechanism.Gemini, FocusDomainConventions.C11ConnectionEndpoint, @"USB\VID_1A86&PID_7523\C11-GEMINI-001", null),
            45000,
            new FocusMotionLimits(0, 100000, 200, 1000, FocusApproachDirection.IncreasingSteps, 50),
            new FocusMetricEvidence(FocusMetricKind.G3StellarShape, g3Id, 2.3, "FWHM pixels", new string('1', 64)),
            lockedUtc.AddMinutes(-10), lockedUtc.AddDays(2), 0.95),
        new(
            FocusDomainRole.Gs350WideField,
            "ManualOperator",
            FocusDomainConventions.Gs350LogicalDeviceId,
            new FocusPhysicalBinding(FocusMechanism.ToupTekAaf, FocusDomainConventions.Gs350ConnectionEndpoint, @"USB\VID_0547&PID_14AD\GS350-AAF-001", "PCIROOT(0)#USBROOT(0)#USB(3)"),
            18000,
            new FocusMotionLimits(0, 50000, 0, 0, FocusApproachDirection.None, 0),
            new FocusMetricEvidence(FocusMetricKind.QhyStellarShapeAndPlateSolve, qhyId, 2.1, "FWHM pixels", new string('2', 64)),
            lockedUtc.AddMinutes(-8), lockedUtc.AddDays(2), 0.93),
        new(
            FocusDomainRole.UvexSpectral,
            FocusDomainConventions.UvexOwner,
            FocusDomainConventions.UvexLogicalDeviceId,
            new FocusPhysicalBinding(FocusMechanism.UvexM2, FocusDomainConventions.UvexConnectionEndpoint, @"USB\VID_1A86&PID_7523\UVEX4-COM5-001", null),
            12000,
            new FocusMotionLimits(-50000, 50000, 100, 500, FocusApproachDirection.IncreasingSteps, 25),
            new FocusMetricEvidence(FocusMetricKind.AtrSpectralLineWidth, atrId, 2.7, "FWHM pixels", new string('3', 64)),
            lockedUtc.AddMinutes(-6), lockedUtc.AddDays(2), 0.97),
    ];

    private static IReadOnlyList<LiveFocusDomainState> CreateLiveFocusStates(NightSetupRecord setup) =>
        setup.FocusDomains!.Select(binding => new LiveFocusDomainState(
            binding.Role,
            binding.Owner,
            binding.LogicalDeviceId,
            binding.PhysicalBinding,
            binding.StartPositionSteps)).ToArray();

    private static LiveSetupState CreateLiveSetup(IReadOnlyList<LiveFocusDomainState> focusDomains) => new(
        "atr", 100, 256, 1, 1, -10, "High Conversion Gain",
        2, -1923, 12000, "c11+ccdt67+slit+2210", "g3", "qhy", focusDomains);
}
