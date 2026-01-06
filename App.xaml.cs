using System;
using System.Windows;
using Velopack;

namespace LegendBorn;

public partial class App : Application
{
    [STAThread]
    private static void Main(string[] args)
    {
        // ВАЖНО: Velopack hooks должны выполниться ДО старта WPF UI
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}