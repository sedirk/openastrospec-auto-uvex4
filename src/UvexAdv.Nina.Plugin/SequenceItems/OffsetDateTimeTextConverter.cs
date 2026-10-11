using System.Globalization;
using Newtonsoft.Json;

namespace UvexAdv.Nina.Plugin.SequenceItems;

/// <summary>
/// N.I.N.A. first loads sequence JSON into JObjects, which promotes ISO text to
/// date tokens before populating plugin properties. Preserve the represented
/// instant instead of letting a date token become culture-specific, zoneless text.
/// </summary>
public sealed class OffsetDateTimeTextConverter : JsonConverter<string>
{
    public override string ReadJson(JsonReader reader, Type objectType, string? existingValue,
        bool hasExistingValue, JsonSerializer serializer) => reader.Value switch
    {
        DateTimeOffset offset => offset.ToString("O", CultureInfo.InvariantCulture),
        // Unspecified stays unspecified: validation must still reject a missing zone.
        DateTime date => date.ToString("O", CultureInfo.InvariantCulture),
        string text => text,
        null => string.Empty,
        _ => throw new JsonSerializationException("Night deadline must be HH:mm clock text or offset-bearing date text."),
    };

    public override void WriteJson(JsonWriter writer, string? value, JsonSerializer serializer) =>
        writer.WriteValue(value);
}
