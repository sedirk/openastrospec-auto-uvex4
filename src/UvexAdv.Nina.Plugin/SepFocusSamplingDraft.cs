using System.Windows.Input;
using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

/// <summary>Only visible sampling fields; cannot change travel limits, safety, owners or acceptance.</summary>
public sealed record SepFocusSamplingDraft(int Frames, int ExposureMilliseconds, int GainPercent)
{
    public SepMainFocusOptions ApplyTo(SepMainFocusOptions current) => current with
    { Frames = Frames, ExposureMilliseconds = ExposureMilliseconds, GainPercent = GainPercent };
}

internal sealed class SepFocusSaveCommand(Func<SepMainFocusOptions> read, Action<SepMainFocusOptions> save,
    Func<bool> canEdit) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => canEdit() &&
        (parameter is null ? read().ConfigurationIssue is null :
            parameter is SepFocusSamplingDraft draft && draft.ApplyTo(read()).ConfigurationIssue is null);
    public void Execute(object? parameter)
    {
        if (CanExecute(parameter)) save(parameter is SepFocusSamplingDraft draft ? draft.ApplyTo(read()) : read());
    }
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
