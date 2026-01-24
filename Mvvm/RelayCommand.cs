// File: Mvvm/RelayCommand.cs
using System;
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
        _execute(parameter);
    }

    public void RaiseCanExecuteChanged()
    {
        var handler = CanExecuteChanged;
        if (handler == null) return;

        try
        {
            var dispatcher = _uiDispatcher ?? Application.Current?.Dispatcher;

            // В тестах/консоли/ранней инициализации может быть null — вызываем напрямую.
            if (dispatcher == null)
            {
                try { handler(this, EventArgs.Empty); } catch { /* ignore */ }
                return;
            }

            // Если диспетчер уже завершает работу — не лезем в UI (иначе прилетит исключение).
            if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
                return;

            if (dispatcher.CheckAccess())
            {
                try { handler(this, EventArgs.Empty); } catch { /* ignore */ }
                return;
            }

            // Всегда на UI поток (чтобы WPF не делал UpdateCanExecute на фоне)
            dispatcher.BeginInvoke(
                DispatcherPriority.DataBind,
                new Action(() =>
                {
                    try { handler(this, EventArgs.Empty); }
                    catch { /* игнорируем, чтобы не рушить UI */ }
                }));
        }
        catch
        {
            // Ни при каких условиях не валим UI из RaiseCanExecuteChanged
        }
    }
}
