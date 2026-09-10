using System;
using Velopack;

namespace AutoShazam;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Must run before any WPF/UI code: handles Velopack's install/update/uninstall hooks.
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
