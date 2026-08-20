using System.Windows;
using Microsoft.Win32;
using LegendBorn.ViewModels;

namespace LegendBorn.Views.Tabs;

public partial class SettingsTabView
{
    private void SelectJava_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
            return;

        var dialog = new OpenFileDialog
        {
            Title = "Выберите Java",
            Filter = "Java (javaw.exe;java.exe)|javaw.exe;java.exe|Программы (*.exe)|*.exe",
            CheckFileExists = true,
            Multiselect = false
        };

        if (!string.IsNullOrWhiteSpace(vm.JavaCustomPath))
        {
            try
            {
                var directory = System.IO.Path.GetDirectoryName(vm.JavaCustomPath);
                if (!string.IsNullOrWhiteSpace(directory) && System.IO.Directory.Exists(directory))
                    dialog.InitialDirectory = directory;
            }
            catch { }
        }

        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;

        vm.UseCustomJava = true;
        vm.JavaCustomPath = dialog.FileName;
    }
}
