using System.Windows;
using AutoShazam.ViewModels;

namespace AutoShazam.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void MicrophonesButton_Click(object sender, RoutedEventArgs e)
    {
        var viewModel = (MainViewModel)DataContext;
        new DeviceListWindow("Microphones", viewModel.Microphones) { Owner = this }.ShowDialog();
    }

    private void SpeakersButton_Click(object sender, RoutedEventArgs e)
    {
        var viewModel = (MainViewModel)DataContext;
        new DeviceListWindow("Speakers", viewModel.Speakers) { Owner = this }.ShowDialog();
    }
}
