// File: Mvvm/RelayCommand.cs
using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace LegendBorn.Mvvm;

public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    // Фиксируем UI-диспетчер на момент создания (обычно команды создаются в UI потоке).
    // Но если Application.Current ещё не готов — доберёмся позже через Application.Current?.Dispatcher.
    private readonly Dispatcher? _uiDispatcher;

    // Коалесцируем частые RaiseCanExecuteChanged
    private int _raiseScheduled; // 0/1

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        if (execute == null) throw new ArgumentNullException(nameof(execute));

        _uiDispatcher = Application.Current?.Dispatcher;

        _execute = _ => execute();
        if (canExecute != null)
            _canExecute = _ => canExecute();
    }

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;

        _uiDispatcher = Application.Current?.Dispatcher;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter)
    {
        try
        {
            return _canExecute?.Invoke(parameter) ?? true;
        }
        catch
        {
            // Не даём исключению сломать WPF командную систему
            return false;
        }
    }

    public void Execute(object? parameter)
    {
        try
        {
            _execute(parameter);
        }
        catch (Exception ex)
        {
            // RelayCommand обычно не должен валить UI.
            Debug.WriteLine(ex);
        }
    }

    public void RaiseCanExecuteChanged()
    {
        var handler = CanExecuteChanged;
        if (handler is null) return;

        // если уже запланировано — выходим
        if (System.Threading.Interlocked.Exchange(ref _raiseScheduled, 1) == 1)
            return;

        try
        {
            var dispatcher = _uiDispatcher ?? Application.Current?.Dispatcher;

            // В тестах/консоли/ранней инициализации может быть null — вызываем напрямую.
            if (dispatcher is null)
            {
                System.Threading.Interlocked.Exchange(ref _raiseScheduled, 0);
                try { handler(this, EventArgs.Empty); } catch { /* ignore */ }
                TryInvalidateRequerySuggested();
                return;
            }

            // Если диспетчер уже завершает работу — не лезем в UI (иначе прилетит исключение).
            if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
            {
                System.Threading.Interlocked.Exchange(ref _raiseScheduled, 0);
                return;
            }

            if (dispatcher.CheckAccess())
            {
                System.Threading.Interlocked.Exchange(ref _raiseScheduled, 0);
                try { handler(this, EventArgs.Empty); } catch { /* ignore */ }
                TryInvalidateRequerySuggested();
                return;
            }

            dispatcher.BeginInvoke(
                DispatcherPriority.DataBind,
                new Action(() =>
                {
                    System.Threading.Interlocked.Exchange(ref _raiseScheduled, 0);
                    try { handler(this, EventArgs.Empty); }
                    catch { /* игнорируем, чтобы не рушить UI */ }
                    TryInvalidateRequerySuggested();
                }));
        }
        catch
        {
            System.Threading.Interlocked.Exchange(ref _raiseScheduled, 0);
            // Ни при каких условиях не валим UI из RaiseCanExecuteChanged
        }
    }

    private static void TryInvalidateRequerySuggested()
    {
        try { CommandManager.InvalidateRequerySuggested(); } catch { /* ignore */ }
    }
}
