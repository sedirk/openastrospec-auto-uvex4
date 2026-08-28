using System.Text.Json;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class FiniteEvidenceDoubleJsonConverterTests
{
    [Fact]
    public void DiagnosticEvidenceWritesNonFiniteSolverSentinelsAsNull()
    {
        var options = new JsonSerializerOptions
        {
            Converters = { new FiniteEvidenceDoubleJsonConverter() },
        };

        var json = JsonSerializer.Serialize(new
        {
            finite = 2.24,
            failedResidual = double.PositiveInfinity,
            failedProjection = double.NegativeInfinity,
            unavailableMetric = double.NaN,
            nested = new[] { 1d, double.NaN },
        }, options);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(2.24, root.GetProperty("finite").GetDouble(), 12);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("failedResidual").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("failedProjection").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("unavailableMetric").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("nested")[1].ValueKind);
    }
}
