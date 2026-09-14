namespace AutoShazam.Services.Audio;

/// <summary>Shared reference points for interpreting dBFS level readings across the app.</summary>
internal static class AudioLevelConstants
{
    /// <summary>Floor for the loudness glow scale on the level icon - at/below this, it shows no
    /// glow at all. Purely cosmetic (there's no silence-detection logic left to configure).</summary>
    public const double GlowFloorDbFs = -50;

    /// <summary>
    /// A "sensible maximum" for level meter purposes - reaching this is already quite loud.
    /// True 0 dBFS (digital full-scale) is rarely hit by normal audio and would make any level
    /// meter that scales against it look perpetually dim.
    /// </summary>
    public const double FullBrightnessDbFs = -12;
}
