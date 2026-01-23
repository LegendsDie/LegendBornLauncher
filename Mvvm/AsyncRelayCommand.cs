using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace LegendBorn.Mvvm;

public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<CancellationToken, Task> _execute;
    private readonly Func<bool>? _canExecute;
    private readonly Action<Exception>? _onError;

    private CancellationTokenSource? _cts;
    private int _isRunning; // 0/1 (Interlocked)

    public AsyncRelayCommand(
        Func<Task> execute,
        Func<bool>? canExecute = null,
        Action<Exception>? onError = null)
        : this(execute: _ => execute(), canExecute: canExecute, onError: onError)
    {
        if (execute is null) throw new ArgumentNullException(nameof(execute));
    }

    public AsyncRelayCommand(
        Func<CancellationToken, Task> execute,
        Func<bool>? canExecute = null,
        Action<Exception>? onError = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
        _onError = onError;
    }

    public event EventHandler? CanExecuteChanged;

    public bool IsRunning => Volatile.Read(ref _isRunning) == 1;

    public bool CanCancel => _cts is not null && !_cts.IsCancellationRequested;

    public bool CanExecute(object? parameter)
        => !IsRunning && (_canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
        => await ExecuteAsync().ConfigureAwait(false);

    public async Task ExecuteAsync(CancellationToken externalToken = default)
    {
        if (!TryBeginExecute())
            return;

        using var linked = CreateLinkedCts(externalToken);
        RaiseCanExecuteChanged();

        try
        {
            await _execute(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            // отмена — штатная ситуация
        }
        catch (Exception ex)
        {
            if (_onError is not null)
            {
                try { _onError(ex); } catch { /* не роняем UI */ }
            }
            else
            {
                Debug.WriteLine(ex);
            }
        }
        finally
        {
            EndExecute();
            RaiseCanExecuteChanged();
        }
    }

    public void Cancel()
    {
        try { _cts?.Cancel(); } catch { }
        RaiseCanExecuteChanged();
    }

    public void RaiseCanExecuteChanged()
        => RaiseCanExecuteChangedOnUi();

    private bool TryBeginExecute()
    {
        if (Interlocked.CompareExchange(ref _isRunning, 1, 0) != 0)
            return false;

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        return true;
    }

    private void EndExecute()
    {
        try { _cts?.Dispose(); } catch { }
        _cts = null;
        Interlocked.Exchange(ref _isRunning, 0);
    }

    private CancellationTokenSource CreateLinkedCts(CancellationToken externalToken)
    {
        if (!externalToken.CanBeCanceled)
            return CancellationTokenSource.CreateLinkedTokenSource(_cts!.Token);

        return CancellationTokenSource.CreateLinkedTokenSource(_cts!.Token, externalToken);
    }

    private void RaiseCanExecuteChangedOnUi()
    {
        var handler = CanExecuteChanged;
        if (handler is null) return;

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            handler(this, EventArgs.Empty);
            TryInvalidateRequerySuggested();
            return;
        }

        dispatcher.BeginInvoke(new Action(() =>
        {
            handler(this, EventArgs.Empty);
            TryInvalidateRequerySuggested();
        }));
    }

    private static void TryInvalidateRequerySuggested()
    {
        try { CommandManager.InvalidateRequerySuggested(); } catch { /* ignore */ }
    }
}

public sealed class AsyncRelayCommand<T> : ICommand
{
    private readonly Func<T?, CancellationToken, Task> _execute;
    private readonly Func<T?, bool>? _canExecute;
    private readonly Action<Exception>? _onError;

    private CancellationTokenSource? _cts;
    private int _isRunning;

    public AsyncRelayCommand(
        Func<T?, Task> execute,
        Func<T?, bool>? canExecute = null,
        Action<Exception>? onError = null)
        : this(execute: (p, _) => execute(p), canExecute: canExecute, onError: onError)
    {
        if (execute is null) throw new ArgumentNullException(nameof(execute));
    }

    public AsyncRelayCommand(
        Func<T?, CancellationToken, Task> execute,
        Func<T?, bool>? canExecute = null,
        Action<Exception>? onError = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
        _onError = onError;
    }

    public event EventHandler? CanExecuteChanged;

    public bool IsRunning => Volatile.Read(ref _isRunning) == 1;

    public bool CanExecute(object? parameter)
    {
        if (IsRunning) return false;

        var p = parameter is T t ? t : default;
        return _canExecute?.Invoke(p) ?? true;
    }

    public async void Execute(object? parameter)
    {
        var p = parameter is T t ? t : default;
        await ExecuteAsync(p).ConfigureAwait(false);
    }

    public async Task ExecuteAsync(T? parameter, CancellationToken externalToken = default)
    {
        if (!TryBeginExecute())
            return;

        using var linked = CreateLinkedCts(externalToken);
        RaiseCanExecuteChanged();

        try
        {
            await _execute(parameter, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (_onError is not null)
            {
                try { _onError(ex); } catch { }
            }
            else
            {
                Debug.WriteLine(ex);
            }
        }
        finally
        {
            EndExecute();
            RaiseCanExecuteChanged();
        }
    }

    public void Cancel()
    {
        try { _cts?.Cancel(); } catch { }
        RaiseCanExecuteChanged();
    }

    public void RaiseCanExecuteChanged()
        => RaiseCanExecuteChangedOnUi();

    private bool TryBeginExecute()
    {
        if (Interlocked.CompareExchange(ref _isRunning, 1, 0) != 0)
            return false;

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        return true;
    }

    private void EndExecute()
    {
        try { _cts?.Dispose(); } catch { }
        _cts = null;
        Interlocked.Exchange(ref _isRunning, 0);
    }

    private CancellationTokenSource CreateLinkedCts(CancellationToken externalToken)
    {
        if (!externalToken.CanBeCanceled)
            return CancellationTokenSource.CreateLinkedTokenSource(_cts!.Token);

        return CancellationTokenSource.CreateLinkedTokenSource(_cts!.Token, externalToken);
    }

    private void RaiseCanExecuteChangedOnUi()
    {
        var handler = CanExecuteChanged;
        if (handler is null) return;

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            handler(this, EventArgs.Empty);
            TryInvalidateRequerySuggested();
            return;
        }

        dispatcher.BeginInvoke(new Action(() =>
        {
            handler(this, EventArgs.Empty);
            TryInvalidateRequerySuggested();
        }));
    }

    private static void TryInvalidateRequerySuggested()
    {
        try { CommandManager.InvalidateRequerySuggested(); } catch { }
    }
}
