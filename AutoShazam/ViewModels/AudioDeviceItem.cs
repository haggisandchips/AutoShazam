using AutoShazam.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AutoShazam.ViewModels;

/// <summary>
/// A capture or render device paired with whether the user has marked it "offered" in Settings -
/// i.e. whether it shows up in the right-click device picker on the microphone/speaker icon.
/// </summary>
public sealed partial class AudioDeviceItem : ObservableObject
{
    public string Id { get; }
    public string Name { get; }
    public AudioSourceKind Kind { get; }

    [ObservableProperty]
    private bool isOffered;

    public AudioDeviceItem(AudioDeviceOption option, bool isOffered)
    {
        Id = option.Id;
        Name = option.Name;
        Kind = option.Kind;
        this.isOffered = isOffered;
    }
}
