using System.Windows;
using AutoShazam.ViewModels;

namespace AutoShazam.Views;

/// <summary>A small popup listing one device kind's "offered" checkboxes - split out of Settings
/// itself so a PC with many microphones/speakers doesn't turn Settings into a long scroll.</summary>
public partial class DeviceListWindow : Window
{
    public DeviceListWindow(string title, IEnumerable<AudioDeviceItem> devices)
    {
        InitializeComponent();
        HeaderText.Text = title;
        DeviceList.ItemsSource = devices;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
