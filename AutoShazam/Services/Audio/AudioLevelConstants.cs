namespace AutoShazam.Services.Audio;

/// <summary>Shared reference points for interpreting dBFS level readings across the app.</summary>
internal static class AudioLevelConstants
{
    /// <summary>Below this, <see cref="SilenceGapDetector"/> considers the signal silent. Now
    /// configurable in Settings (see <see cref="AutoShazam.Models.AppSettings.SilenceThresholdDbFs"/>) -
    /// this is just the default for a fresh install.</summary>
    public const double SilenceThresholdDbFs = -45;

    /// <summary>
    /// A "sensible maximum" for level meter purposes - reaching this is already quite loud.
    /// True 0 dBFS (digital full-scale) is rarely hit by normal audio and would make any level
    /// meter that scales against it look perpetually dim.
    /// </summary>
    public const double FullBrightnessDbFs = -12;
}
