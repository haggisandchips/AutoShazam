using System.ComponentModel;
using System.Windows;
using AutoShazam.ViewModels;

namespace AutoShazam.Views;

public partial class LyricsWindow : Window
{
    public LyricsWindow()
    {
        InitializeComponent();

        // Grows to fit the lyrics (no scrolling) up to this cap, so it never grows off-screen.
        MaxHeight = Math.Max(MinHeight, SystemParameters.WorkArea.Height - 80);

        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is INotifyPropertyChanged oldVm)
        {
            oldVm.PropertyChanged -= OnViewModelPropertyChanged;
        }

        if (e.NewValue is INotifyPropertyChanged newVm)
        {
            newVm.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    // Auto-scrolls the synced-lyrics list as the estimated playback position advances.
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.CurrentLyricLineIndex) || DataContext is not MainViewModel vm)
        {
            return;
        }

        int index = vm.CurrentLyricLineIndex;
        if (index < 0)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (SyncedLyricsList.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement container)
            {
                container.BringIntoView();
            }
        });
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
