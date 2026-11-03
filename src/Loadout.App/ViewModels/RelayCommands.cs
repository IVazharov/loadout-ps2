using System.Windows.Input;

namespace Loadout.App;

public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => _execute();

    public event EventHandler? CanExecuteChanged;

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class AsyncRelayCommand : ICommand, IDisposable
{
    private readonly Func<CancellationToken, Task> _execute;
    private readonly Action<Exception>? _onError;
    private readonly object _gate = new();
    private CancellationTokenSource? _run;
    private Task _tail = Task.CompletedTask;

    public AsyncRelayCommand(Func<Task> execute, Action<Exception>? onError = null)
        : this(IgnoreToken(execute), onError)
    {
    }

    private static Func<CancellationToken, Task> IgnoreToken(Func<Task> execute)
    {
        ArgumentNullException.ThrowIfNull(execute);
        return _ => execute();
    }

    public AsyncRelayCommand(Func<CancellationToken, Task> execute, Action<Exception>? onError = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _onError = onError;
    }

    public bool CanExecute(object? parameter) => true;

    /// <summary>Cancels the run that is still going, if any.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _run?.Cancel();
            _run?.Dispose();
            _run = null;
        }
    }

    public async void Execute(object? parameter)
    {
        Task mine;
        lock (_gate)
        {
            _run?.Cancel();
            _run?.Dispose();
            _run = new CancellationTokenSource();
            var token = _run.Token;
            var prior = _tail;
            mine = InvokeAsync(prior, token);
            _tail = mine;
        }

        try
        {
            await mine;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _onError?.Invoke(ex);
        }
    }

    private async Task InvokeAsync(Task prior, CancellationToken token)
    {
        try
        {
            await prior;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
        }

        if (token.IsCancellationRequested)
        {
            return;
        }

        try
        {
            await _execute(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _onError?.Invoke(ex);
        }
    }

    public event EventHandler? CanExecuteChanged;

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
