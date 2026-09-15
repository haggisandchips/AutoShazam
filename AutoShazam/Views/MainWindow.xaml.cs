using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using AutoShazam.Models;
using AutoShazam.Services.Settings;
using AutoShazam.ViewModels;

namespace AutoShazam.Views;

public partial class MainWindow : Window
{
    // Must match the fixed Height set on each line in the lyrics preview's DataTemplate.
    private const double LyricsPreviewLineHeight = 28;

    // Slower than the normal per-line scroll (see NormalLyricsScrollDuration) - matched by
    // MainViewModel.LyricsPreviewFirstLineLead, which keeps the panel blank long enough beforehand
    // for this to still settle before the line goes current.
    private static readonly TimeSpan FirstLineScrollDuration = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan NormalLyricsScrollDuration = TimeSpan.FromMilliseconds(220);

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

        _viewModel.PropertyChanged += ViewModel_PropertyChanged;

        RestoreWindowPlacement();
        RestoreArtPanelSplit();

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
        Closed += (_, _) =>
        {
            _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
            _viewModel.Dispose();
        };
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.PreviewLyricLineIndex))
        {
            AnimateLyricsPreview();
        }
    }

    // Tracks the previous value so the entrance from blank (see AnimateLyricsPreview) can be told
    // apart from every other, much shorter, step.
    private int _lastPreviewLyricLineIndex = -2;

    private void AnimateLyricsPreview()
    {
        int newIndex = _viewModel.PreviewLyricLineIndex;
        int previousIndex = _lastPreviewLyricLineIndex;
        _lastPreviewLyricLineIndex = newIndex;

        // +2 accounts for the leading 84px (3-line) blank spacer in the ScrollViewer's content -
        // see MainWindow.xaml - so offset 0 shows nothing, and each step afterward centers one
        // more line.
        double targetOffset = (newIndex + 2) * LyricsPreviewLineHeight;

        if (newIndex <= -2)
        {
            // A reset (new track, or the result was cleared) - the index only ever goes back to
            // -2 via an explicit reset, never as part of normal forward progress, so snap instantly
            // instead of animating backward from wherever the panel happened to be.
            LyricsPreviewScroll.BeginAnimation(ScrollViewerOffsetAnimation.VerticalOffsetProperty, null);
            ScrollViewerOffsetAnimation.SetVerticalOffset(LyricsPreviewScroll, targetOffset);
            return;
        }

        // The very first line's entrance from blank is a longer scroll than the usual step between
        // lines, so it reads as a deliberate reveal rather than a snap.
        bool isFirstLineEntrance = previousIndex <= -2;
        var animation = new DoubleAnimation
        {
            To = targetOffset,
            Duration = isFirstLineEntrance ? FirstLineScrollDuration : NormalLyricsScrollDuration,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut },
        };
        LyricsPreviewScroll.BeginAnimation(ScrollViewerOffsetAnimation.VerticalOffsetProperty, animation);
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

    private void RestoreArtPanelSplit()
    {
        // Clamp defensively: a hand-edited or otherwise out-of-range stored ratio shouldn't be
        // able to collapse either half to nothing.
        double ratio = Math.Clamp(_viewModel.Settings.ArtPanelSplitRatio, 0.15, 0.85);
        ArtColumn.Width = new GridLength(ratio, GridUnitType.Star);
        InfoColumn.Width = new GridLength(1 - ratio, GridUnitType.Star);
    }

    private void ArtPanelSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        double artStars = ArtColumn.Width.Value;
        double infoStars = InfoColumn.Width.Value;
        double total = artStars + infoStars;
        if (total <= 0)
        {
            return;
        }

        var settings = _viewModel.Settings;
        settings.ArtPanelSplitRatio = artStars / total;
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

    // Left-click a source icon to select it; right-click to pick a specific device from the
    // (filtered) list offered for that kind - see AudioDeviceItem.IsOffered / Settings.

    private void MicIconHost_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => _viewModel.SelectMicrophoneCommand.Execute(null);

    private void SpeakerIconHost_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => _viewModel.SelectSpeakerCommand.Execute(null);

    private void MicIconHost_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var selectedId = _viewModel.SelectedMicrophoneDevice?.Id;
        MicDeviceList.ItemsSource = _viewModel.GetOfferedDevices(AudioSourceKind.Microphone)
            .Select(d => new DevicePickerRow(d, d.Id == selectedId))
            .ToList();
        MicDevicePopup.IsOpen = true;
        e.Handled = true;
    }

    private void SpeakerIconHost_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var selectedId = _viewModel.SelectedSpeakerDevice?.Id;
        SpeakerDeviceList.ItemsSource = _viewModel.GetOfferedDevices(AudioSourceKind.Speaker)
            .Select(d => new DevicePickerRow(d, d.Id == selectedId))
            .ToList();
        SpeakerDevicePopup.IsOpen = true;
        e.Handled = true;
    }

    private void MicDeviceItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: AudioDeviceItem device })
        {
            _viewModel.PickMicrophoneDevice(device);
        }

        MicDevicePopup.IsOpen = false;
    }

    private void SpeakerDeviceItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: AudioDeviceItem device })
        {
            _viewModel.PickSpeakerDevice(device);
        }

        SpeakerDevicePopup.IsOpen = false;
    }

    /// <summary>One row in the right-click device popup - just enough to show a checkmark beside
    /// whichever device is currently selected for that icon's source.</summary>
    private sealed record DevicePickerRow(AudioDeviceItem Device, bool IsSelected);
}
