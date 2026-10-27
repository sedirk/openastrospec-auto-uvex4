using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class ProductionEvidenceSerializationTests
{
    [Fact]
    public void AllCompiledAnonymousEvidenceShapesSerializeWithActualProductionOptions()
    {
        var options = (JsonSerializerOptions)typeof(RealObservationStageRunner)
            .GetField("EvidenceJsonOptions", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var shapes = typeof(RealObservationStageRunner).Assembly.GetTypes()
            .Where(t => t.Name.Contains("AnonymousType", StringComparison.Ordinal) &&
                t.IsDefined(typeof(CompilerGeneratedAttribute), false)).ToArray();
        Assert.NotEmpty(shapes);
        var checkedTrackReceipt = false;
        foreach (var shape in shapes)
        {
            // Anonymous type property names are independent of its inferred value
            // types. Closing with object/null executes the real writer's naming
            // metadata for every compiled payload, including seldom-used branches.
            var closed = shape.IsGenericTypeDefinition
                ? shape.MakeGenericType(Enumerable.Repeat(typeof(object), shape.GetGenericArguments().Length).ToArray())
                : shape;
            var constructor = Assert.Single(closed.GetConstructors());
            var instance = constructor.Invoke(new object?[constructor.GetParameters().Length]);
            var json = JsonSerializer.Serialize(instance, closed, options);
            using var parsed = JsonDocument.Parse(json);
            var names = parsed.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
            Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
            if (names.Contains("referenceCompletedUtc") && names.Contains("currentCompletedUtc"))
            {
                Assert.Contains("registered", names);
                Assert.Contains("temporal", names);
                checkedTrackReceipt = true;
            }
        }
        Assert.True(checkedTrackReceipt, "The actual production target-track receipt must be covered.");
    }
}
