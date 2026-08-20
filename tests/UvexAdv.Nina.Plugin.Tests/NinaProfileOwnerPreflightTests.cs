using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class NinaProfileOwnerPreflightTests
{
    private static readonly NinaProfileOwnerExpectation Expected = new(
        "ToupTek_ATR585M_STABLE_ID",
        "ASCOM.OnStep.Telescope",
        NinaProfileOwnerPreflight.C11FocuserDeviceId,
        NinaProfileOwnerPreflight.OpticalCoverDeviceId,
        NinaProfileOwnerPreflight.NoPhysicalFilterWheelDeviceId,
        NinaProfileOwnerPreflight.Phd2GuiderName,
        RequireFlatDevice: true);

    [Fact]
    public void ExactStableOwnerBindingsPass()
    {
        var result = NinaProfileOwnerPreflight.Validate(
            MatchingSelection(),
            Expected);

        Assert.Equal(GateDisposition.Passed, result.Disposition);
        Assert.Equal("NINA_PROFILE_OWNERS_PREVALIDATED", result.Code);
    }

    [Theory]
    [InlineData("QHYminiCam8M-fixture-other-owner")]
    [InlineData("ToupTek_G3M2210M_STABLE_ID")]
    [InlineData("")]
    [InlineData(" ToupTek_ATR585M_STABLE_ID")]
    [InlineData("ToupTek_ATR585M_STABLE_ID ")]
    public void WrongCameraIsRejectedBeforeConnection(string selectedCameraId)
    {
        var result = NinaProfileOwnerPreflight.Validate(
            MatchingSelection() with { CameraId = selectedCameraId },
            Expected);

        Assert.Equal(GateDisposition.Failed, result.Disposition);
        Assert.Equal("NINA_PROFILE_OWNER_MISMATCH", result.Code);
        Assert.Contains("ATR camera", result.Message, StringComparison.Ordinal);
        Assert.Contains("No physical Connect was attempted", result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("telescope")]
    [InlineData("focuser")]
    [InlineData("flat")]
    [InlineData("filter-wheel")]
    [InlineData("guider")]
    public void EveryOtherPhysicalOwnerIsStrictlyPrevalidated(string owner)
    {
        var selected = owner switch
        {
            "telescope" => MatchingSelection() with { TelescopeId = "ASCOM.Simulator.Telescope" },
            "focuser" => MatchingSelection() with { FocuserId = "ASCOM.ToupTek.AAF" },
            "flat" => MatchingSelection() with { FlatDeviceId = "ASCOM.Simulator.CoverCalibrator" },
            "filter-wheel" => MatchingSelection() with { FilterWheelId = "QHY CFW" },
            "guider" => MatchingSelection() with { GuiderName = "DirectGuider" },
            _ => throw new ArgumentOutOfRangeException(nameof(owner)),
        };

        var result = NinaProfileOwnerPreflight.Validate(selected, Expected);

        Assert.Equal(GateDisposition.Failed, result.Disposition);
        Assert.Equal("NINA_PROFILE_OWNER_MISMATCH", result.Code);
    }

    [Fact]
    public void UnusedFlatDeviceDoesNotBlockWhenNoFlatConnectCanOccur()
    {
        var result = NinaProfileOwnerPreflight.Validate(
            MatchingSelection() with { FlatDeviceId = "No_Device" },
            Expected with { RequireFlatDevice = false });

        Assert.Equal(GateDisposition.Passed, result.Disposition);
    }

    [Theory]
    [InlineData("PHD2")]
    [InlineData("phd2_single")]
    [InlineData("PHD2_Single ")]
    [InlineData(" PHD2_Single")]
    public void GuiderAdapterUsesExactActiveProfileIdWithOrdinalComparison(string selected)
    {
        var result = NinaProfileOwnerPreflight.Validate(
            MatchingSelection() with { GuiderName = selected },
            Expected);

        Assert.Equal(GateDisposition.Failed, result.Disposition);
        Assert.Contains("N.I.N.A. guider adapter", result.Message, StringComparison.Ordinal);
        Assert.Contains("PHD2_Single", result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("QHYCFW")]
    [InlineData("no_device")]
    [InlineData("No_Device ")]
    [InlineData("")]
    public void PhysicalOrInexactFilterWheelSelectionIsRejectedBeforeConnection(string selected)
    {
        var result = NinaProfileOwnerPreflight.Validate(
            MatchingSelection() with { FilterWheelId = selected },
            Expected);

        Assert.Equal(GateDisposition.Failed, result.Disposition);
        Assert.Contains("filter wheel", result.Message, StringComparison.Ordinal);
        Assert.Contains("No physical Connect was attempted", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RealRunnerExecutesOwnerGateBeforeFirstMediatorConnect()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "Sources",
            "RealObservationStageRunner.cs"));
        var start = source.IndexOf(
            "private async Task<GateResult> EnsureNinaEquipmentConnectedAsync(",
            StringComparison.Ordinal);
        var end = source.IndexOf(
            "private GateResult ValidateNinaProfileOwnerSelections(",
            start,
            StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var body = source[start..end];

        var preflight = body.IndexOf("ValidateNinaProfileOwnerSelections(context.Plan)", StringComparison.Ordinal);
        var firstConnect = body.IndexOf(".Connect()", StringComparison.Ordinal);
        Assert.True(preflight >= 0 && firstConnect > preflight);
        Assert.Contains("ValidatePhdProfileBindingEvidence()", body, StringComparison.Ordinal);
    }

    [Fact]
    public void RealRunnerRechecksOwnerSelectionsAtEveryImmediatePhysicalActionGate()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "Sources",
            "RealObservationStageRunner.cs"));
        var start = source.IndexOf(
            "private GateResult ValidateCurrentActionPrerequisites(",
            StringComparison.Ordinal);
        var end = source.IndexOf(
            "private GateResult ValidateOpticalCoverOpen(",
            start,
            StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var body = source[start..end];

        Assert.Contains("ValidateNinaProfileOwnerSelections(context.Plan)", body, StringComparison.Ordinal);
    }

    private static NinaProfileOwnerSelection MatchingSelection() => new(
        Expected.AtrCameraId,
        Expected.TelescopeId,
        Expected.FocuserId,
        Expected.FlatDeviceId,
        Expected.FilterWheelId,
        Expected.GuiderName);
}
