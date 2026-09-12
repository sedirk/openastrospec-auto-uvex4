using System.Reflection;
using System.Runtime.InteropServices;
using NINA.Core.Enum;
using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class G3DestinationPierSidePreflightTests
{
    public static IEnumerable<object[]> UnsupportedDriverResponses()
    {
        // The installed N.I.N.A. ASCOM assembly recreates the incident's exact
        // exception/message without opening the mount or any driver device.
        yield return [new ASCOM.NotImplementedException("DestinationSideOfPier")];
        yield return [new ASCOM.PropertyNotImplementedException("DestinationSideOfPier", false)];
        yield return [new ASCOM.MethodNotImplementedException("DestinationSideOfPier")];
        yield return [new TargetInvocationException(new ASCOM.NotImplementedException("DestinationSideOfPier"))];
        yield return [new AggregateException(new TargetInvocationException(new ASCOM.NotImplementedException("DestinationSideOfPier")))];
        yield return [new COMException("driver capability unavailable", ASCOM.ErrorCodes.NotImplemented)];
        yield return [new COMException("E_NOTIMPL", unchecked((int)0x80004001))];
    }

    [Theory]
    [MemberData(nameof(UnsupportedDriverResponses))]
    public void AscomCapabilityNotImplementedIsAnExplicitWarningNotAMotionVeto(Exception exception)
    {
        var calls = 0;
        var result = G3DestinationPierSidePreflight.Check("pierEast", () => { calls++; throw exception; });
        Assert.Equal(1, calls);
        Assert.Equal(GateDisposition.Passed, result.Disposition);
        Assert.Equal(GateSeverity.Warning, result.Severity);
        Assert.Equal("G3_DESTINATION_PIER_SIDE_UNAVAILABLE", result.Code);
        Assert.Contains("no same-side prediction", result.Message);
    }

    public static IEnumerable<object[]> FailedDriverResponses()
    {
        yield return [new ASCOM.NotConnectedException("disconnected")];
        yield return [new ASCOM.InvalidValueException("bad coordinate")];
        yield return [new COMException("not connected", ASCOM.ErrorCodes.NotConnected)];
        yield return [new COMException("DestinationSideOfPier is not implemented in this driver.", unchecked((int)0x80004005))];
        yield return [new IOException("DestinationSideOfPier is not implemented in this driver.")];
        yield return [new TargetInvocationException(new TimeoutException("timed out"))];
        yield return [new TimeoutException("timed out", new ASCOM.NotImplementedException("DestinationSideOfPier"))];
        yield return [new AggregateException(new ASCOM.NotImplementedException("DestinationSideOfPier"), new IOException("USB failed"))];
    }

    [Theory]
    [MemberData(nameof(FailedDriverResponses))]
    public void ErrorTextOrNestedCapabilityDoesNotHideConnectionAndTransportFailure(Exception exception)
    {
        var result = G3DestinationPierSidePreflight.Check("pierEast", () => throw exception);
        Assert.Equal(GateDisposition.Indeterminate, result.Disposition);
        Assert.Equal("G3_DESTINATION_PIER_SIDE_QUERY_FAILED", result.Code);
        Assert.Contains(exception.GetType().FullName!, result.Message);
        Assert.Contains($"0x{exception.HResult:X8}", result.Message);
    }

    [Theory]
    [InlineData("pierWest", PierSide.pierEast, false)]
    [InlineData("pierEast", PierSide.pierWest, false)]
    [InlineData("pierEast", PierSide.pierEast, true)]
    [InlineData("pierWest", PierSide.pierWest, true)]
    public void KnownPredictionRejectsCrossSideBeforeDispatch(string expected, PierSide predicted, bool allowed)
    {
        var result = G3DestinationPierSidePreflight.Check(expected, () => predicted);
        Assert.Equal(allowed, result.Disposition == GateDisposition.Passed);
        Assert.Equal(allowed ? "G3_DESTINATION_PIER_SIDE_SAME" : "G3_DESTINATION_PIER_SIDE_CHANGE", result.Code);
    }

    [Fact]
    public void UnavailablePredictionIsNotInventedSameSideAndQueryErrorsDoNotAuthorize()
    {
        foreach (var query in new Func<PierSide>[] { () => PierSide.pierUnknown,
            () => throw new NotImplementedException(), () => throw new NotSupportedException() })
            Assert.Equal("G3_DESTINATION_PIER_SIDE_UNAVAILABLE", G3DestinationPierSidePreflight.Check("pierWest", query).Code);
        Assert.Equal(GateDisposition.Indeterminate, G3DestinationPierSidePreflight.Check("pierWest",
            () => throw new IOException("Driver timeout")).Disposition);
        Assert.Equal(GateDisposition.Indeterminate, G3DestinationPierSidePreflight.Check("pierUnknown",
            () => throw new Exception("must not query")).Disposition);
    }

    [Fact]
    public void ProductionReturnChecksPredictionBeforePrechargeAndAgainBeforeSlew()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "RealObservationStageRunner.cs"));
        var start = source.IndexOf("private async Task<G3AcquisitionMotionReturnResult> ReturnDurableG3AcquisitionToOriginAsync", StringComparison.Ordinal);
        Assert.True(start > 0);
        var first = source.IndexOf("ValidateG3DestinationPierSide(commanded, state.PierSide)", start, StringComparison.Ordinal);
        var intent = source.IndexOf("Phase = G3AcquisitionMotionPhase.ReturnIntent", start, StringComparison.Ordinal);
        var second = source.IndexOf("ValidateG3DestinationPierSide(commanded, state.PierSide)", first + 1, StringComparison.Ordinal);
        var slew = source.IndexOf("telescopeMediator.SlewToCoordinatesAsync(commanded", start, StringComparison.Ordinal);
        Assert.True(first > start && intent > first && second > intent && slew > second);
    }

    [Fact]
    public void WcsMotionPreflightFailurePreservesReturnButDoesNotFallIntoOpticalSearch()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "RealObservationStageRunner.cs"));
        var start = source.IndexOf("private async Task<StageResult> RunG3WcsCenteringAsync", StringComparison.Ordinal);
        var end = source.IndexOf("return await RunBoundedG3LocalSearchAsync", start, StringComparison.Ordinal);
        var method = source[start..end];
        Assert.Contains("destinationPreflightStopGate = blocked;", method);
        Assert.Contains("destinationPreflightStopGate = destinationSideGate;", method);
        Assert.Contains("motionPreflightFailureCode = destinationPreflightStopGate?.Code", method);
        Assert.Contains("nextRecovery = returned.ReturnedToOrigin && !localSearchBudgetExhausted && destinationPreflightStopGate is null", method);
        var returned = method.IndexOf("var returned = await ReturnDurableG3AcquisitionToOriginAsync", StringComparison.Ordinal);
        var check = method.IndexOf("if (destinationPreflightStopGate is not null)", returned, StringComparison.Ordinal);
        var stop = method.IndexOf("return new StageResult(", check, StringComparison.Ordinal);
        var capture = method.IndexOf("var originField = await CaptureAndAnalyzeG3WithSolveLadderAsync", check, StringComparison.Ordinal);
        Assert.True(returned >= 0 && check > returned && stop > check && capture > stop);
        Assert.Contains("destinationPreflightStopGate with", method[stop..capture]);
        Assert.Contains("[\"g3LocalSearchAuthorized\"] = bool.FalseString", method[stop..capture]);
    }
}
