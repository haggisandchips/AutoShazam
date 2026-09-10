using System.Windows;
using AutoShazam.Services.Update;

namespace AutoShazam.Views;

public partial class AboutWindow : Window
{
    public AboutWindow(bool isInstalled)
    {
        InitializeComponent();

        VersionText.Text = $"Version {ReleaseInfo.GetVersion()}";

        ReleaseDateText.Text = isInstalled
            ? $"Released {ReleaseInfo.GetReleaseDate() ?? "date unknown"}"
            : "Local development build";
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
