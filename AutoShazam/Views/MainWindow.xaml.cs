using System.Windows;
using System.Windows.Media;
using AutoShazam.Services.Settings;
using AutoShazam.ViewModels;

namespace AutoShazam.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly SettingsService _settingsService;

    public MainWindow(MainViewModel viewModel, SettingsService settingsService)
    {
        InitializeComponent();

        _viewModel = viewModel;
        _settingsService = settingsService;
        DataContext = _viewModel;

        RestoreWindowPlacement();

        StateChanged += (_, _) => UpdateMaximizeIcon();
        Closing += (_, _) => SaveWindowPlacement();
        Closed += (_, _) => _viewModel.Dispose();
    }

    private void RestoreWindowPlacement()
    {
        var settings = _viewModel.Settings;

        Width = settings.WindowWidth > 0 ? settings.WindowWidth : Width;
        Height = settings.WindowHeight > 0 ? settings.WindowHeight : Height;

        if (!double.IsNaN(settings.WindowLeft) && !double.IsNaN(settings.WindowTop))
        {
            Left = settings.WindowLeft;
            Top = settings.WindowTop;
            EnsureOnScreen();
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        if (settings.WindowMaximized)
        {
            WindowState = WindowState.Maximized;
        }

        UpdateMaximizeIcon();
    }

    private void EnsureOnScreen()
    {
        double virtualLeft = SystemParameters.VirtualScreenLeft;
        double virtualTop = SystemParameters.VirtualScreenTop;
        double virtualWidth = SystemParameters.VirtualScreenWidth;
        double virtualHeight = SystemParameters.VirtualScreenHeight;

        if (Left + Width < virtualLeft + 40 || Left > virtualLeft + virtualWidth - 40)
        {
            Left = virtualLeft + Math.Max(0, (virtualWidth - Width) / 2);
        }

        if (Top < virtualTop || Top > virtualTop + virtualHeight - 40)
        {
            Top = virtualTop + Math.Max(0, (virtualHeight - Height) / 2);
        }
    }

    private void SaveWindowPlacement()
    {
        var settings = _viewModel.Settings;
        bool maximized = WindowState == WindowState.Maximized;

        settings.WindowMaximized = maximized;

        // RestoreBounds hold the pre-maximize geometry even while maximized/minimized.
        var bounds = maximized || WindowState == WindowState.Minimized ? RestoreBounds : new Rect(Left, Top, Width, Height);
        if (bounds.Width > 0 && bounds.Height > 0)
        {
            settings.WindowLeft = bounds.Left;
            settings.WindowTop = bounds.Top;
            settings.WindowWidth = bounds.Width;
            settings.WindowHeight = bounds.Height;
        }

        _settingsService.Save(settings);
    }

    private void UpdateMaximizeIcon()
    {
        MaximizeIcon.Data = WindowState == WindowState.Maximized
            ? Geometry.Parse("M2,0 L10,0 L10,8 L8,8 M0,2 L8,2 L8,10 L0,10 Z")
            : Geometry.Parse("M0,0 L10,0 L10,10 L0,10 Z");
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var settingsWindow = new SettingsWindow
        {
            Owner = this,
            DataContext = _viewModel,
        };
        settingsWindow.ShowDialog();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
