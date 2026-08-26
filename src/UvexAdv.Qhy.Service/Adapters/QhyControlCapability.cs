namespace UvexAdv.Qhy.Service.Adapters;

using UvexAdv.Qhy.Core;

/// <summary>
/// Identity-bound proof that a required native QHY control existed when the
/// exact camera handle was initialized. Some QHY SDK builds transiently return
/// "unavailable" for an otherwise working control after a single-frame read;
/// the hardware capability does not change during the lifetime of that handle.
/// </summary>
internal readonly record struct QhyControlCapability(
    int ControlId,
    string Name,
    double Minimum,
    double Maximum,
    double Step)
{
    public static QhyControlCapability Create(
        int controlId,
        string name,
        double minimum,
        double maximum,
        double step)
    {
        if (controlId < 0) throw new ArgumentOutOfRangeException(nameof(controlId));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A QHY control name is required.", nameof(name));
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || maximum < minimum)
        {
            throw new QhyAdapterException(
                $"QHY {name} reported invalid bounds [{minimum:G15}, {maximum:G15}].");
        }
        if (!double.IsFinite(step) || step < 0)
        {
            throw new QhyAdapterException($"QHY {name} reported invalid step {step:G15}.");
        }

        return new QhyControlCapability(controlId, name.Trim(), minimum, maximum, step);
    }

    public void ValidateRequestedValue(double value)
    {
        if (!double.IsFinite(value) || value < Minimum || value > Maximum)
        {
            throw new QhyAdapterException(
                $"Requested QHY {Name} {value:G15} is outside [{Minimum:G15}, {Maximum:G15}].");
        }
    }
}
