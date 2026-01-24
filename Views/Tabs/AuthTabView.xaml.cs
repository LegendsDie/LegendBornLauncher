// File: Views/Tabs/AuthTabView.xaml.cs
using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using LegendBorn.ViewModels;

namespace LegendBorn.Views.Tabs;

public partial class AuthTabView : UserControl
{
    private bool _copyBusy;

    public AuthTabView()
    {
        InitializeComponent();
    }

    private async void CopyOrRegenLoginLink_OnClick(object sender, RoutedEventArgs e)
    {
        if (_copyBusy) return;
        _copyBusy = true;

        try
        {
            var vm = TryGetMainVm();
            if (vm == null) return;

            // Если ссылка уже есть — просто копируем
            if (vm.HasLoginUrl)
            {
                if (vm.CopyLoginUrlCommand?.CanExecute(null) == true)
                    vm.CopyLoginUrlCommand.Execute(null);
                return;
            }

            // Иначе — инициируем логин через сайт (VM должен начать получать URL)
            if (vm.LoginViaSiteCommand?.CanExecute(null) == true)
                vm.LoginViaSiteCommand.Execute(null);

            // Ждём появления ссылки (до ~4.5 сек)
            for (var i = 0; i < 30; i++)
            {
                await Task.Delay(150).ConfigureAwait(true);

                if (vm.HasLoginUrl)
                {
                    if (vm.CopyLoginUrlCommand?.CanExecute(null) == true)
                        vm.CopyLoginUrlCommand.Execute(null);
                    return;
                }
            }

            MessageBox.Show(
                "Не удалось получить ссылку авторизации. Попробуйте ещё раз.",
                "Авторизация",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch
        {
            // ignored
        }
        finally
        {
            _copyBusy = false;
        }
    }

    private MainViewModel? TryGetMainVm()
    {
        // 1) Если DataContext уже VM
        if (DataContext is MainViewModel vm)
            return vm;

        // 2) Иначе пробуем получить VM из окна-хоста
        var w = Window.GetWindow(this);
        if (w?.DataContext is MainViewModel winVm)
            return winVm;

        return null;
    }
}
