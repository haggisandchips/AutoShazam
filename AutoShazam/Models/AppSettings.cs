namespace AutoShazam.Models;

/// <summary>Persisted user settings. Every change is written to disk immediately (see the On*Changed
/// partial methods on <see cref="AutoShazam.ViewModels.MainViewModel"/>), and restored as-is on startup.</summary>
public sealed class AppSettings
{
    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public double WindowWidth { get; set; } = 960;
    public double WindowHeight { get; set; } = 640;
    public bool WindowMaximized { get; set; }

    public string? SelectedMicrophoneDeviceId { get; set; }
    public string? SelectedSpeakerDeviceId { get; set; }

    /// <summary>Which of the two is actively captured - restored on startup rather than always
    /// defaulting to one, since the user's choice of source is a durable preference now that
    /// there's no separate Auto Shazam toggle.</summary>
    public AudioSourceKind ActiveAudioSource { get; set; } = AudioSourceKind.Microphone;

    /// <summary>Device IDs the user has marked as "offered" in Settings - these are what populate
    /// the right-click picker on the microphone/speaker icons. Empty means nothing has been
    /// curated yet, in which case every device of the relevant kind is offered.</summary>
    public HashSet<string> OfferedDeviceIds { get; set; } = new();

    /// <summary>Whether to silently check for updates on startup (installed release builds only).</summary>
    public bool AutomaticallyCheckForUpdates { get; set; } = true;
}
