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
        // Velopack hooks до старта WPF UI
        VelopackApp.Build().Run();

        // Bootstrap settings ДО создания UI/VM/окон
        SettingsBootstrapper.Bootstrap();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}