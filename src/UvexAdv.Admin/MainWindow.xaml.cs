using System.ComponentModel;

namespace UvexAdv.Admin;

public partial class MainWindow
{
    private readonly MainViewModel viewModel = new(new UvexApiClient(new Uri("http://127.0.0.1:47844")));
    private bool closeInProgress;
    private bool cleanupCompleted;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += async (_, _) => await viewModel.StartAsync();
        Closing += OnClosing;
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (cleanupCompleted)
        {
            return;
        }

        e.Cancel = true;
        if (closeInProgress)
        {
            return;
        }

        closeInProgress = true;
        IsEnabled = false;
        try
        {
            await viewModel.DisposeAsync();
        }
        catch (Exception ex)
        {
            // Never let an async close handler tear down the dispatcher. DisposeAsync already
            // records expected cleanup failures; this is the final diagnostic boundary.
            viewModel.AddDiagnostic($"窗口关闭清理异常：{ex.Message}");
        }
        finally
        {
            cleanupCompleted = true;
            closeInProgress = false;
            Close();
        }
    }
}
