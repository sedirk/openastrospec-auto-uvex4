using System.Windows.Input;

namespace UvexAdv.Nina.Plugin;

/// <summary>Only the four visible target fields; no device, safety or run settings.</summary>
public sealed record ObservationTargetDraft(
    string TargetName,
    string CatalogId,
    double? RightAscensionDegrees,
    double? DeclinationDegrees)
{
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(TargetName) && TargetName.Length <= 256 &&
        CatalogId is not null && CatalogId.Length <= 256 &&
        RightAscensionDegrees is { } ra && double.IsFinite(ra) && ra >= 0 && ra < 360 &&
        DeclinationDegrees is { } dec && double.IsFinite(dec) && dec >= -90 && dec <= 90;

    public ObservationTargetImportResult ToImportResult()
    {
        if (!IsValid) throw new ArgumentException("Target requires a name and valid J2000 coordinates in degrees.");
        var coordinates = new ObservationTargetCoordinates(RightAscensionDegrees!.Value, DeclinationDegrees!.Value);
        return new(TargetName.Trim(), CatalogId.Trim(), coordinates.RightAscensionDegrees,
            coordinates.DeclinationDegrees, "J2000 target draft", DateTimeOffset.UtcNow,
            ObservationTargetImportService.J2000Epoch, null,
            "Explicit target draft applied through the visible target-plan command; equipment and safety settings are unchanged.",
            coordinates, null, null, false);
    }
}

internal sealed class ObservationTargetDraftCommand(
    Func<ObservationTargetDraft> readVisibleDraft,
    Action<ObservationTargetDraft> apply,
    Func<bool> canEdit) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canEdit() &&
        (parameter is null ? readVisibleDraft().IsValid : parameter is ObservationTargetDraft { IsValid: true });

    public void Execute(object? parameter)
    {
        if (CanExecute(parameter)) apply(parameter as ObservationTargetDraft ?? readVisibleDraft());
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
