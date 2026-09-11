using System.Windows;
using AutoShazam.Services.Settings;
using AutoShazam.Services.Update;
using AutoShazam.ViewModels;
using AutoShazam.Views;

namespace AutoShazam;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var updateService = new AppUpdateService();
        var appDataRoot = AppPaths.GetAppDataRoot(updateService.IsInstalled);
        var settingsService = new SettingsService(appDataRoot);
        var settings = settingsService.Load();

        var viewModel = new MainViewModel(settings, updateService, settingsService, appDataRoot);
        var window = new MainWindow(viewModel, settingsService);
        MainWindow = window;
        window.Show();

        _ = viewModel.CheckForUpdatesOnStartupAsync();
    }
}
