namespace AutoShazam.Services.Audio;

/// <summary>
/// Tracks the microphone level stream and raises <see cref="GapEnded"/> when audio resumes
/// after a sustained quiet period - the kind of gap found between tracks on an LP, CD, or
/// streamed album. A short "resume" debounce avoids false triggers on brief pops/clicks.
/// </summary>
internal sealed class SilenceGapDetector
{
    public double SilenceThresholdDbFs { get; set; } = AudioLevelConstants.SilenceThresholdDbFs;
    public TimeSpan MinSilenceDuration { get; set; } = TimeSpan.FromMilliseconds(1200);
    public TimeSpan ResumeConfirmDuration { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>How long continuous silence is tolerated before <see cref="ExtendedSilenceDetected"/> fires.</summary>
    public TimeSpan ExtendedSilenceThreshold { get; set; } = TimeSpan.FromSeconds(15);

    private TimeSpan _quietAccum;
    private TimeSpan _loudAccum;
    private bool _inGap;
    private bool _extendedSilenceRaised;

    public event EventHandler? GapEnded;

    /// <summary>Raised once per continuous silent stretch once it reaches <see cref="ExtendedSilenceThreshold"/>.</summary>
    public event EventHandler? ExtendedSilenceDetected;

    public void Reset()
    {
        _quietAccum = TimeSpan.Zero;
        _loudAccum = TimeSpan.Zero;
        _inGap = false;
        _extendedSilenceRaised = false;
    }

    public void OnLevelSample(double dbFs, TimeSpan duration)
    {
        bool isQuiet = dbFs < SilenceThresholdDbFs;

        if (isQuiet)
        {
            _quietAccum += duration;
            _loudAccum = TimeSpan.Zero;
            if (_quietAccum >= MinSilenceDuration)
            {
                _inGap = true;
            }

            if (!_extendedSilenceRaised && _quietAccum >= ExtendedSilenceThreshold)
            {
                _extendedSilenceRaised = true;
                ExtendedSilenceDetected?.Invoke(this, EventArgs.Empty);
            }

            return;
        }

        _quietAccum = TimeSpan.Zero;
        _extendedSilenceRaised = false;
        if (!_inGap)
        {
            return;
        }

        _loudAccum += duration;
        if (_loudAccum < ResumeConfirmDuration)
        {
            return;
        }

        _inGap = false;
        _loudAccum = TimeSpan.Zero;
        GapEnded?.Invoke(this, EventArgs.Empty);
    }
}
