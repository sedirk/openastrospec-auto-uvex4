using System.Globalization;

namespace UvexAdv.Protocol;

/// <summary>A single UVEX serial frame. The wire form is :CODE;arg;arg;#.</summary>
public sealed record UvexFrame(string Code, IReadOnlyList<string> Arguments, string Raw)
{
    public bool TryGetInt32(int index, out int value)
    {
        value = default;
        return index >= 0 && index < Arguments.Count &&
            int.TryParse(Arguments[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    public bool TryGetDouble(int index, out double value)
    {
        value = default;
        return index >= 0 && index < Arguments.Count &&
            double.TryParse(Arguments[index], NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}
