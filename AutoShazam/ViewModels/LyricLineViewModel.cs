using CommunityToolkit.Mvvm.ComponentModel;

namespace AutoShazam.ViewModels;

/// <summary>One line in the synced-lyrics panel; <see cref="IsCurrent"/> is flipped on/off by
/// <see cref="MainViewModel"/>'s sync timer as the estimated playback position advances.</summary>
public sealed partial class LyricLineViewModel : ObservableObject
{
    public string Text { get; }

    [ObservableProperty]
    private bool isCurrent;

    public LyricLineViewModel(string text)
    {
        Text = text;
    }
}
