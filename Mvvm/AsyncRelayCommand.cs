// File: Mvvm/AsyncRelayCommand.cs
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace LegendBorn.Mvvm;

public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<CancellationToken, Task> _execute;
    private readonly Func<bool>? _canExecute;
    private readonly Action<Exception>? _onError;

    // IMPORTANT: в WPF изменения CanExecute/PropertyChanged должны триггериться с UI
    private readonly Dispatcher? _dispatcher;

    private CancellationTokenSource? _cts;
    private int _isRunning; // 0/1 (Interlocked)
    private int _raiseScheduled; // коалесцируем частые RaiseCanExecuteChanged

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

        try { _dispatcher = Application.Current?.Dispatcher; }
        catch { _dispatcher = null; }
    }

    public event EventHandler? CanExecuteChanged;

    public bool IsRunning => Volatile.Read(ref _isRunning) == 1;

    public bool CanCancel
    {
        get
        {
            var cts = Volatile.Read(ref _cts);
            return cts is not null && !cts.IsCancellationRequested;
        }
    }

    public bool CanExecute(object? parameter)
        => !IsRunning && (_canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
        => await ExecuteAsync().ConfigureAwait(true);

    /// <summary>
    /// Выполняет команду. ВАЖНО: не используем ConfigureAwait(false),
    /// чтобы продолжения по умолчанию возвращались на UI-поток (WPF).
    /// </summary>
    public async Task ExecuteAsync(CancellationToken externalToken = default)
    {
        if (!TryBeginExecute())
            return;

        using var linked = CreateLinkedCts(externalToken);
        RaiseCanExecuteChanged();

        try
        {
            // ВАЖНО: без ConfigureAwait(false) — чтобы VM после await продолжала на UI.
            await _execute(linked.Token).ConfigureAwait(true);
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
        try { Volatile.Read(ref _cts)?.Cancel(); } catch { /* ignore */ }
        RaiseCanExecuteChanged();
    }

    public void RaiseCanExecuteChanged()
        => RaiseCanExecuteChangedOnUiCoalesced();

    private bool TryBeginExecute()
    {
        if (Interlocked.CompareExchange(ref _isRunning, 1, 0) != 0)
            return false;

        var newCts = new CancellationTokenSource();
        var prev = Interlocked.Exchange(ref _cts, newCts);
        try { prev?.Dispose(); } catch { /* ignore */ }

        return true;
    }

    private void EndExecute()
    {
        var prev = Interlocked.Exchange(ref _cts, null);
        try { prev?.Dispose(); } catch { /* ignore */ }

        Interlocked.Exchange(ref _isRunning, 0);
    }

    private CancellationTokenSource CreateLinkedCts(CancellationToken externalToken)
    {
        var local = Volatile.Read(ref _cts) ?? throw new InvalidOperationException("Command CTS not initialized.");

        if (!externalToken.CanBeCanceled)
            return CancellationTokenSource.CreateLinkedTokenSource(local.Token);

        return CancellationTokenSource.CreateLinkedTokenSource(local.Token, externalToken);
    }

    private void RaiseCanExecuteChangedOnUiCoalesced()
    {
        var handler = CanExecuteChanged;
        if (handler is null) return;

        // если диспетчера нет или мы уже в UI — поднимаем сразу
        if (_dispatcher is null || _dispatcher.CheckAccess())
        {
            handler(this, EventArgs.Empty);
            TryInvalidateRequerySuggested();
            return;
        }

        // коалесцируем частые вызовы
        if (Interlocked.Exchange(ref _raiseScheduled, 1) == 1)
            return;

        _dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(() =>
        {
            Interlocked.Exchange(ref _raiseScheduled, 0);
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

    private readonly Dispatcher? _dispatcher;

    private CancellationTokenSource? _cts;
    private int _isRunning;
    private int _raiseScheduled;

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

        try { _dispatcher = Application.Current?.Dispatcher; }
        catch { _dispatcher = null; }
    }

    public event EventHandler? CanExecuteChanged;

    public bool IsRunning => Volatile.Read(ref _isRunning) == 1;

    public bool CanCancel
    {
        get
        {
            var cts = Volatile.Read(ref _cts);
            return cts is not null && !cts.IsCancellationRequested;
        }
    }

    public bool CanExecute(object? parameter)
    {
        if (IsRunning) return false;

        var p = parameter is T t ? t : default;
        return _canExecute?.Invoke(p) ?? true;
    }

    public async void Execute(object? parameter)
    {
        var p = parameter is T t ? t : default;
        await ExecuteAsync(p).ConfigureAwait(true);
    }

    public async Task ExecuteAsync(T? parameter, CancellationToken externalToken = default)
    {
        if (!TryBeginExecute())
            return;

        using var linked = CreateLinkedCts(externalToken);
        RaiseCanExecuteChanged();

        try
        {
            // без ConfigureAwait(false) — чтобы возвращаться на UI
            await _execute(parameter, linked.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (_onError is not null)
            {
                try { _onError(ex); } catch { /* ignore */ }
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
        try { Volatile.Read(ref _cts)?.Cancel(); } catch { /* ignore */ }
        RaiseCanExecuteChanged();
    }

    public void RaiseCanExecuteChanged()
        => RaiseCanExecuteChangedOnUiCoalesced();

    private bool TryBeginExecute()
    {
        if (Interlocked.CompareExchange(ref _isRunning, 1, 0) != 0)
            return false;

        var newCts = new CancellationTokenSource();
        var prev = Interlocked.Exchange(ref _cts, newCts);
        try { prev?.Dispose(); } catch { /* ignore */ }

        return true;
    }

    private void EndExecute()
    {
        var prev = Interlocked.Exchange(ref _cts, null);
        try { prev?.Dispose(); } catch { /* ignore */ }

        Interlocked.Exchange(ref _isRunning, 0);
    }

    private CancellationTokenSource CreateLinkedCts(CancellationToken externalToken)
    {
        var local = Volatile.Read(ref _cts) ?? throw new InvalidOperationException("Command CTS not initialized.");

        if (!externalToken.CanBeCanceled)
            return CancellationTokenSource.CreateLinkedTokenSource(local.Token);

        return CancellationTokenSource.CreateLinkedTokenSource(local.Token, externalToken);
    }

    private void RaiseCanExecuteChangedOnUiCoalesced()
    {
        var handler = CanExecuteChanged;
        if (handler is null) return;

        if (_dispatcher is null || _dispatcher.CheckAccess())
        {
            handler(this, EventArgs.Empty);
            TryInvalidateRequerySuggested();
            return;
        }

        if (Interlocked.Exchange(ref _raiseScheduled, 1) == 1)
            return;

        _dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(() =>
        {
            Interlocked.Exchange(ref _raiseScheduled, 0);
            handler(this, EventArgs.Empty);
            TryInvalidateRequerySuggested();
        }));
    }

    private static void TryInvalidateRequerySuggested()
    {
        try { CommandManager.InvalidateRequerySuggested(); } catch { /* ignore */ }
    }
}
