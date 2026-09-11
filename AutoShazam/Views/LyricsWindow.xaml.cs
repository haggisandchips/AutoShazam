using System.Windows;

namespace AutoShazam.Views;

public partial class LyricsWindow : Window
{
    public LyricsWindow()
    {
        InitializeComponent();

        // Grows to fit the lyrics (no scrolling) up to this cap, so it never grows off-screen.
        MaxHeight = Math.Max(MinHeight, SystemParameters.WorkArea.Height - 80);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
