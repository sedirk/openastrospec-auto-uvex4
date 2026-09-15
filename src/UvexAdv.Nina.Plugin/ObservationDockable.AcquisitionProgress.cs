using System.Windows.Threading;
using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

public sealed partial class ObservationDockable
{
    private DispatcherTimer? acquisitionProgressTimer;
    private ObservationDashboardSnapshot? acquisitionDashboard;
    public ObservationAcquisitionPresentation Acquisition { get; private set; } = ObservationAcquisitionPresentation.Empty;

    private void ApplyAcquisitionDashboard(ObservationDashboardSnapshot dashboard)
    {
        acquisitionDashboard = dashboard;
        RefreshAcquisitionPresentation();
        if (dashboard.AcquisitionProgress is not null && dashboard.Run.State == ObservationRunState.RunningAuto)
        {
            if (acquisitionProgressTimer is null)
            {
                acquisitionProgressTimer = new DispatcherTimer(DispatcherPriority.Background)
                { Interval = TimeSpan.FromSeconds(1) };
                acquisitionProgressTimer.Tick += (_, _) => RefreshAcquisitionPresentation();
            }
            acquisitionProgressTimer.Start();
        }
        else acquisitionProgressTimer?.Stop();
    }

    private void RefreshAcquisitionPresentation()
    {
        if (acquisitionDashboard is not { } dashboard) return;
        // Presentation clock only. No camera polling, persistence or device commands.
        Acquisition = ObservationAcquisitionPresentation.Build(dashboard.AcquisitionProgress,
            dashboard.Run.ObservationRunId, dashboard.Run.State, DateTimeOffset.UtcNow, UiCulture);
        RaisePropertyChanged(nameof(Acquisition));
    }
}
