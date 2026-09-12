using System.Reflection;
using System.Runtime.InteropServices;
using NINA.Core.Enum;
using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

/// <summary>Prediction only. Never changes pier side, owns a device, or settles motion.</summary>
internal static class G3DestinationPierSidePreflight
{
    public static GateResult Check(string expectedSide, Func<PierSide> predict)
    {
        if (!Enum.TryParse<PierSide>(expectedSide, out var expected) ||
            expected is not (PierSide.pierEast or PierSide.pierWest))
            return GateResult.Unknown("G3_DESTINATION_PIER_SIDE_UNKNOWN", "The durable origin pier side is unknown.");
        PierSide predicted;
        try { predicted = predict(); }
        catch (Exception ex) when (IsPredictionUnavailable(ex))
        {
            predicted = PierSide.pierUnknown;
        }
        catch (Exception ex)
        {
            return GateResult.Unknown("G3_DESTINATION_PIER_SIDE_QUERY_FAILED",
                $"N.I.N.A. destination-side prediction failed; no slew authorized by this preflight: {DescribeException(ex)}");
        }
        if (predicted is not (PierSide.pierEast or PierSide.pierWest))
            // Preserve legacy support on drivers without a prediction API.
            // This is NOT a same-side assertion: actual side, fresh binding,
            // budgets and post-command readback remain mandatory in the runner.
            return GateResult.Warn("G3_DESTINATION_PIER_SIDE_UNAVAILABLE",
                "Driver destination-side prediction unavailable; no same-side prediction was made.");
        return predicted == expected
            ? GateResult.Pass("G3_DESTINATION_PIER_SIDE_SAME", $"Driver predicts {predicted}; actual post-command side still requires verification.")
            : GateResult.Unknown("G3_DESTINATION_PIER_SIDE_CHANGE",
                $"Driver predicts {predicted}, but the durable motion origin is {expected}. No cross-pier slew was dispatched; the return obligation remains outstanding.");
    }

    private static bool IsPredictionUnavailable(Exception exception, int depth = 0)
    {
        if (depth >= 8) return false;
        // ASCOM.NotImplementedException is NOT System.NotImplementedException.
        // Use the actual host exception hierarchy (including method/property
        // subclasses), not English error text or a short type-name guess.
        if (exception is System.NotImplementedException or NotSupportedException or ASCOM.NotImplementedException)
            return true;
        if (exception is COMException com)
            return com.ErrorCode == ASCOM.ErrorCodes.NotImplemented ||
                com.ErrorCode == unchecked((int)0x80004001); // E_NOTIMPL
        // Only transparent wrappers may be unwrapped. A timeout with an inner
        // capability exception, or a mixed aggregate, is still a real failure.
        return exception switch
        {
            TargetInvocationException { InnerException: { } inner } => IsPredictionUnavailable(inner, depth + 1),
            AggregateException aggregate when aggregate.InnerExceptions.Count == 1 =>
                IsPredictionUnavailable(aggregate.InnerExceptions[0], depth + 1),
            _ => false,
        };
    }

    private static string DescribeException(Exception exception, int depth = 0)
    {
        var description = $"{exception.GetType().FullName} (0x{exception.HResult:X8}): {exception.Message}";
        return depth < 7 && exception.InnerException is { } inner
            ? description + " -> " + DescribeException(inner, depth + 1)
            : description;
    }
}
