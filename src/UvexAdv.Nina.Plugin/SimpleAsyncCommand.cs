using System.Windows.Input;

namespace UvexAdv.Nina.Plugin;

internal sealed class SimpleAsyncCommand(Func<Task> execute, Func<bool>? canExecute = null) : ICommand
{
    private bool busy;
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => !busy && (canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (busy) return;
        busy = true;
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try { await execute().ConfigureAwait(true); }
        finally { busy = false; CanExecuteChanged?.Invoke(this, EventArgs.Empty); }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
