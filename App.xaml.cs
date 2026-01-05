using System;
using System.Windows;
using Velopack;

namespace LegendBorn;

public partial class App : Application
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Velopack должен выполниться ДО открытия окна (обновления / relaunch)
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}