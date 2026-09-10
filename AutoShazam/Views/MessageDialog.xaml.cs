using System.Windows;

namespace AutoShazam.Views;

/// <summary>
/// A dark-themed replacement for MessageBox.Show. Also fixes a real reliability issue: a native
/// MessageBox shown from a nested submenu item's Click handler (even deferred via
/// Dispatcher.BeginInvoke at ApplicationIdle priority) reliably opened invisible and never
/// returned in testing, while a plain custom Window in the same spot works fine.
/// </summary>
public partial class MessageDialog : Window
{
    public bool Result { get; private set; }

    private MessageDialog(string title, string message, bool isConfirmation)
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;

        if (isConfirmation)
        {
            PrimaryButton.Content = "Yes";
            SecondaryButton.Content = "No";
            SecondaryButton.Visibility = Visibility.Visible;
        }
    }

    public static void ShowInfo(Window? owner, string title, string message)
    {
        var dialog = new MessageDialog(title, message, isConfirmation: false) { Owner = owner };
        dialog.ShowDialog();
    }

    /// <summary>Shows a Yes/No confirmation and returns true if the user chose Yes.</summary>
    public static bool ShowConfirm(Window? owner, string title, string message)
    {
        var dialog = new MessageDialog(title, message, isConfirmation: true) { Owner = owner };
        dialog.ShowDialog();
        return dialog.Result;
    }

    private void Primary_Click(object sender, RoutedEventArgs e)
    {
        Result = true;
        Close();
    }

    private void Secondary_Click(object sender, RoutedEventArgs e) => Close();

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
