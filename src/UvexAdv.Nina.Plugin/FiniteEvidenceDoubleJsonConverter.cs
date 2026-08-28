using System.Text.Json;
using System.Text.Json.Serialization;

namespace UvexAdv.Nina.Plugin;

/// <summary>
/// Diagnostic evidence is strict JSON.  A failed solver or geometry probe may
/// legitimately report NaN/Infinity internally, but that sentinel must never
/// turn evidence publication into a stage failure.  Canonical motion ledgers
/// deliberately do not use this converter and remain fail-closed.
/// </summary>
internal sealed class FiniteEvidenceDoubleJsonConverter : JsonConverter<double>
{
    public override double Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options) => reader.GetDouble();

    public override void Write(
        Utf8JsonWriter writer,
        double value,
        JsonSerializerOptions options)
    {
        if (double.IsFinite(value))
        {
            writer.WriteNumberValue(value);
            return;
        }

        writer.WriteNullValue();
    }
}
