using System.Globalization;

namespace UvexAdv.Protocol;

public sealed record UvexCommand(
    string Code,
    IReadOnlyList<string> Arguments,
    bool ExpectsResponse = false,
    bool CausesMotion = false,
    bool IsEmergency = false)
{
    public string ToWireString()
    {
        if (Code.Length != 4 || !Code.All(char.IsLetterOrDigit))
        {
            throw new InvalidOperationException($"Invalid UVEX command code '{Code}'.");
        }

        var args = Arguments.Count == 0 ? string.Empty : string.Join(';', Arguments) + ';';
        return $":{Code.ToUpperInvariant()};{args}#";
    }

    public static UvexCommand Query(string code) => new(code, Array.Empty<string>(), ExpectsResponse: true);

    public static UvexCommand WithInt(string code, int value, bool causesMotion = false) =>
        new(code, [value.ToString(CultureInfo.InvariantCulture)], CausesMotion: causesMotion);
}
