using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using AutoShazam.Services.Settings;
using AutoShazam.ViewModels;

namespace AutoShazam.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly SettingsService _settingsService;

    // Position/size change while dragging or resizing fire continuously - debounce those into a
    // single write shortly after the user stops, rather than hitting the database on every pixel.
    private readonly DispatcherTimer _placementSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };

    public MainWindow(MainViewModel viewModel, SettingsService settingsService)
    {
        InitializeComponent();

        _viewModel = viewModel;
        _settingsService = settingsService;
        DataContext = _viewModel;

        RestoreWindowPlacement();

        _placementSaveTimer.Tick += (_, _) =>
        {
            _placementSaveTimer.Stop();
            SaveWindowPlacement();
        };

        LocationChanged += (_, _) => SchedulePlacementSave();
        SizeChanged += (_, _) => SchedulePlacementSave();
        StateChanged += (_, _) =>
        {
            UpdateMaximizeIcon();
            // Maximize/minimize/restore are discrete, infrequent events - save immediately rather
            // than waiting out the drag/resize debounce.
            _placementSaveTimer.Stop();
            SaveWindowPlacement();
        };
        Closing += (_, _) =>
        {
            _placementSaveTimer.Stop();
            SaveWindowPlacement();
        };
        Closed += (_, _) => _viewModel.Dispose();
    }

    private void SchedulePlacementSave()
    {
        _placementSaveTimer.Stop();
        _placementSaveTimer.Start();
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

        // RestoreBounds hold the pre-maximize geometry even while maximized/minimized - avoids
        // persisting the off-screen coordinates Windows gives a minimized window, or the
        // full-monitor bounds of a maximized one.
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

    private void SettingsMenuItem_Click(object sender, RoutedEventArgs e)
        // Deferred for the same reason as the other nested File submenu items - give the menu's
        // capture/popup teardown a full chance to drain before showing a modal dialog.
        => Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            var settingsWindow = new SettingsWindow
            {
                Owner = this,
                DataContext = _viewModel,
            };
            settingsWindow.ShowDialog();
        }));

    private void CheckForUpdateMenuItem_Click(object sender, RoutedEventArgs e)
        // Deferred: showing a modal dialog synchronously from inside a MenuItem.Click handler can
        // collide with the Menu's own capture/popup teardown - more so here since this is a nested
        // submenu item (File > Check for Update), so ApplicationIdle priority gives it a slot only
        // once that's fully drained.
        => Dispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            new Action(() => _ = _viewModel.CheckForUpdatesManuallyAsync()));

    private void ExitMenuItem_Click(object sender, RoutedEventArgs e) => Close();

    private void AboutMenuItem_Click(object sender, RoutedEventArgs e)
        // Deferred at ApplicationIdle for the same reason as CheckForUpdateMenuItem_Click - this
        // is a nested item (Auto Shazam > About...), so give the menu's capture/popup teardown a
        // full chance to drain before showing a modal dialog.
        => Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            var aboutWindow = new AboutWindow(_viewModel.IsInstalled) { Owner = this };
            aboutWindow.ShowDialog();
        }));

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
