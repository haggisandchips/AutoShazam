using AutoShazam.Services.Audio;

namespace AutoShazam.Models;

/// <summary>
/// Persisted user settings. Deliberately does NOT include the Auto Shazam toggle state -
/// that control always defaults to off on startup, by design.
/// </summary>
public sealed class AppSettings
{
    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public double WindowWidth { get; set; } = 960;
    public double WindowHeight { get; set; } = 640;
    public bool WindowMaximized { get; set; }
    public string? SelectedMicrophoneDeviceId { get; set; }

    /// <summary>How long Auto Shazam will tolerate continuous silence before switching itself off.</summary>
    public double ExtendedSilenceTimeoutSeconds { get; set; } = 15;

    /// <summary>
    /// The dBFS level below which the microphone input is considered silent. Less negative
    /// (e.g. -30) requires louder sound to count as "not silence," ignoring faint background
    /// noise; more negative (e.g. -60) picks up even very quiet sounds.
    /// </summary>
    public double SilenceThresholdDbFs { get; set; } = AudioLevelConstants.SilenceThresholdDbFs;

    /// <summary>
    /// How long (in milliseconds) the sound level has to sit on one side of the silence threshold
    /// before the sound-level icon/tooltip commits to it. Smooths out flicker from borderline-quiet
    /// noises that hover right around the threshold.
    /// </summary>
    public double SoundStateDebounceMs { get; set; } = 200;

    /// <summary>Whether to silently check for updates on startup (installed release builds only).</summary>
    public bool AutomaticallyCheckForUpdates { get; set; } = true;
}
