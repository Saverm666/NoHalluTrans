using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace NoHalluTrans.Desktop;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; Changed(name); return true;
    }
    protected void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
internal interface IRefreshableCommand { void Refresh(); }
internal static class CommandHub
{
    private static readonly List<WeakReference<IRefreshableCommand>> Commands = [];
    public static void Register(IRefreshableCommand command) => Commands.Add(new(command));
    public static void Refresh()
    {
        for (int i = Commands.Count - 1; i >= 0; i--)
            if (Commands[i].TryGetTarget(out var command)) command.Refresh(); else Commands.RemoveAt(i);
    }
}
public sealed class RelayCommand : ICommand, IRefreshableCommand
{
    private readonly Action execute; private readonly Func<bool>? enabled;
    public RelayCommand(Action execute, Func<bool>? enabled = null)
    { this.execute = execute; this.enabled = enabled; CommandHub.Register(this); }
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => enabled?.Invoke() ?? true;
    public void Execute(object? parameter) { if (CanExecute(parameter)) execute(); }
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
public sealed class AsyncCommand : ICommand, IRefreshableCommand
{
    private readonly Func<Task> execute; private readonly Action<Exception> onError; private readonly Func<bool>? enabled;
    private bool running;
    public AsyncCommand(Func<Task> execute, Action<Exception> onError, Func<bool>? enabled = null)
    { this.execute = execute; this.onError = onError; this.enabled = enabled; CommandHub.Register(this); }
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => !running && (enabled?.Invoke() ?? true);
    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        running = true; CommandHub.Refresh();
        try { await execute(); } catch (Exception ex) { onError(ex); }
        finally { running = false; CommandHub.Refresh(); }
    }
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
