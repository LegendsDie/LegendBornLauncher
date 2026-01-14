using System;
using System.Windows;
using Velopack;
using LegendBorn.Services;

namespace LegendBorn;

public partial class App : Application
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Velopack hooks должны вызываться до старта WPF UI.
        // В релизе не даём этому уронить запуск (на случай странной среды/падения native части).
        try
        {
            VelopackApp.Build().Run();
        }
        catch
        {
            // ignore (release-safe)
        }

        // Bootstrap settings ДО создания UI/VM/окон
        try
        {
            SettingsBootstrapper.Bootstrap();
        }
        catch
        {
            // ignore (release-safe)
        }

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}