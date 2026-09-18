using AutoShazam.Models;
using AutoShazam.Services.Audio;
using AutoShazam.Services.Diagnostics;
using AutoShazam.Services.Settings;
using AutoShazam.Services.Shazam;

namespace AutoShazam.Services.Recognition;

/// <summary>
/// Orchestrates recognition attempts (manual button clicks and Auto Shazam's periodic checks)
/// through a single gate, so Shazam is never queried concurrently or in a tight loop:
/// - Only one recognition runs at a time.
/// - While Auto Shazam is on, checks repeat on a self-paced interval, all sourced from
///   <see cref="TuningConfig"/> (overridable without a release, but deliberately not a
///   user-facing setting): never more often than <see cref="_minQueryInterval"/>, normally at
///   that same interval whenever the last attempt didn't confirm what's currently playing, backed
///   off to the independently-configured <see cref="_knownSongQueryInterval"/> once it has (no
///   need to re-confirm a track that's presumably still playing as often as we poll while we have
///   no idea what's on) - and backed off further still (up to <see cref="MaxQueryInterval"/>)
///   whenever a response signals we're going too fast - see <see cref="ShazamClient"/>'s header
///   inspection - or a request fails outright, so a persistent problem doesn't turn into a tight
///   retry loop.
/// - A manual button click always attempts to run (subject only to the busy guard), and by
///   construction always performs exactly one recognition, using whatever source/device is
///   currently selected regardless of whether Auto Shazam is on.
/// </summary>
internal sealed class RecognitionCoordinator : IDisposable
{
    private static readonly TimeSpan RecognitionClipDuration = TimeSpan.FromSeconds(9);
    private static readonly TimeSpan MaxQueryInterval = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan PollTickInterval = TimeSpan.FromSeconds(1);

    private readonly AudioCaptureService _capture = new();
    private readonly RecognitionLog _log;
    private readonly ShazamClient _shazamClient;
    private readonly TimeSpan _minQueryInterval;
    private readonly TimeSpan _knownSongQueryInterval;

    private readonly object _sync = new();
    private bool _busy;
    private DateTime _nextAttemptDueUtc = DateTime.MinValue;
    private TimeSpan _currentInterval;
    private bool _autoEnabled;
    private AudioSourceKind _kind = AudioSourceKind.Microphone;
    private string? _deviceId;
    private Timer? _autoPollTimer;

    public event EventHandler? RecognitionStarted;
    public event EventHandler<RecognitionResult>? RecognitionSucceeded;
    public event EventHandler? RecognitionNoMatch;
    public event EventHandler<string>? RecognitionFailed;

    /// <summary>Raised instead of any of the above when an in-flight attempt was cut short by
    /// capture being torn down (e.g. Auto Shazam turned off mid-recording) rather than actually
    /// completing. No message - just a signal that whoever set busy/listening state should clear it.</summary>
    public event EventHandler? RecognitionCancelled;

    /// <summary>Raised whenever capture actually starts or stops (Auto Shazam, or a one-shot
    /// manual recording).</summary>
    public event EventHandler<bool>? SourceActiveChanged;

    /// <summary>The current input level in dBFS, for the level-meter icon. Only meaningful while
    /// the source is active.</summary>
    public event EventHandler<double>? LevelChanged;

    /// <summary>Raised immediately before/after the actual HTTP call to Shazam (a subset of a
    /// full recognition attempt, which also spends time recording audio first).</summary>
    public event EventHandler<bool>? ShazamQueryActiveChanged;

    /// <summary>Raised on every poll tick while Auto Shazam is on and nothing is currently in
    /// flight - a heartbeat the UI can use to settle on an "idle" status between checks.</summary>
    public event EventHandler? Idle;

    /// <summary>Raised when Auto Shazam was already running and had to stop because capture
    /// failed mid-session (e.g. the source was switched to a device that's unavailable). Not
    /// raised for the initial <see cref="SetAutoEnabled"/> failure - that one is thrown back to
    /// the caller synchronously so the toggle never has to be told "actually, no" after the fact.</summary>
    public event EventHandler<string>? AutoStoppedUnexpectedly;

    public RecognitionCoordinator(string appDataRoot)
    {
        var tuning = TuningConfig.Load(appDataRoot);

        _log = new RecognitionLog(appDataRoot);
        _shazamClient = new ShazamClient(_log, tuning.RateLimitFallbackBackoff);

        _minQueryInterval = tuning.MinQueryInterval;
        _knownSongQueryInterval = tuning.KnownSongQueryInterval;
        _currentInterval = _minQueryInterval;

        _capture.LevelSample += (_, e) => LevelChanged?.Invoke(this, e.DbFs);
        _capture.ActiveChanged += (_, active) => SourceActiveChanged?.Invoke(this, active);
    }

    /// <summary>Configures which device to capture from. If Auto Shazam is currently running,
    /// capture is restarted against the new source immediately.</summary>
    public void SetSource(AudioSourceKind kind, string? deviceId)
    {
        lock (_sync)
        {
            _kind = kind;
            _deviceId = deviceId;
        }

        _capture.SetSource(kind, deviceId);

        if (!_autoEnabled)
        {
            return;
        }

        try
        {
            _capture.Restart();
        }
        catch (Exception ex)
        {
            // The old device stopped and the new one didn't come up - don't leave Auto Shazam
            // silently "on" with a dead source.
            StopAutoState();
            AutoStoppedUnexpectedly?.Invoke(this, ex.Message);
        }
    }

    /// <summary>
    /// Enables or disables Auto Shazam. Enabling performs an immediate initial check and starts
    /// the periodic polling loop. If capture fails to start, Auto Shazam is left off and the
    /// exception propagates to the caller (rather than leaving it looking "on" with nothing running).
    /// </summary>
    public void SetAutoEnabled(bool enabled)
    {
        if (_autoEnabled == enabled)
        {
            return;
        }

        if (enabled)
        {
            _capture.Start(); // throws on failure; _autoEnabled stays false
            _autoEnabled = true;
            lock (_sync)
            {
                _currentInterval = _minQueryInterval;
            }

            _autoPollTimer = new Timer(_ => Tick(), null, PollTickInterval, PollTickInterval);
            _ = TryRecognizeAsync(); // initial check
        }
        else
        {
            StopAutoState();
        }
    }

    private void StopAutoState()
    {
        _autoEnabled = false;
        _autoPollTimer?.Dispose();
        _autoPollTimer = null;
        _capture.Stop();
    }

    /// <summary>Manual one-shot recognition (works regardless of Auto Shazam).</summary>
    public Task TriggerManualAsync() => TryRecognizeAsync();

    private void Tick()
    {
        DateTime nextAttemptDue;
        bool busy;
        lock (_sync)
        {
            nextAttemptDue = _nextAttemptDueUtc;
            busy = _busy;
        }

        if (busy)
        {
            return;
        }

        // Fires every tick while Auto Shazam has nothing in flight - lets the UI settle on an
        // "Idle" status rather than leaving the previous attempt's outcome message shown forever
        // during the gap until the next check.
        Idle?.Invoke(this, EventArgs.Empty);

        if (DateTime.UtcNow >= nextAttemptDue)
        {
            _ = TryRecognizeAsync();
        }
    }

    private async Task TryRecognizeAsync()
    {
        lock (_sync)
        {
            if (_busy)
            {
                return;
            }

            _busy = true;
        }

        RecognitionStarted?.Invoke(this, EventArgs.Empty);
        _log.Write("Attempt started.");

        try
        {
            var clip = await _capture.RecordSnippetAsync(RecognitionClipDuration, CancellationToken.None)
                .ConfigureAwait(false);

            ShazamQueryActiveChanged?.Invoke(this, true);
            ShazamRecognizeOutcome outcome;
            try
            {
                outcome = await _shazamClient.RecognizeAsync(clip.Samples, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                ShazamQueryActiveChanged?.Invoke(this, false);
            }

            lock (_sync)
            {
                _currentInterval = outcome.RateLimitedRetryAfter is { } retryAfter
                    ? Clamp(Max(retryAfter, _currentInterval * 2), _minQueryInterval, MaxQueryInterval)
                    : outcome.Match is not null ? _knownSongQueryInterval : _minQueryInterval;
                _nextAttemptDueUtc = DateTime.UtcNow + Jittered(_currentInterval);
            }

            if (outcome.Match is null)
            {
                _log.Write("Attempt outcome: no match.");
                RecognitionNoMatch?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                _log.Write($"Attempt outcome: matched '{outcome.Match.Title}' by '{outcome.Match.Artist}'.");
                RecognitionSucceeded?.Invoke(
                    this,
                    new RecognitionResult(outcome.Match.Title, outcome.Match.Artist, outcome.Match.CoverArtUrl, outcome.Match.OffsetSeconds, clip.StartedUtc));
            }
        }
        catch (CaptureStoppedException)
        {
            // Capture was torn down mid-recording (e.g. Auto Shazam was switched off while this
            // attempt was in flight) - not a real failure, nothing to report. Deliberately NOT
            // catching the broader OperationCanceledException here: HttpClient throws that same
            // base type on its own request timeout, and a genuine network failure should be
            // surfaced via RecognitionFailed below, not silently swallowed as routine teardown.
            _log.Write("Attempt outcome: cancelled (capture stopped mid-recording).");
            RecognitionCancelled?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            lock (_sync)
            {
                _currentInterval = Clamp(_currentInterval * 2, _minQueryInterval, MaxQueryInterval);
                _nextAttemptDueUtc = DateTime.UtcNow + Jittered(_currentInterval);
            }

            _log.Write($"Attempt outcome: failed - {ex}");
            RecognitionFailed?.Invoke(this, ex.Message);
        }
        finally
        {
            lock (_sync)
            {
                _busy = false;
            }
        }
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max)
        => value < min ? min : (value > max ? max : value);

    /// <summary>Randomizes an interval by up to +/-20% - Auto Shazam polling at an exact, unvarying
    /// period is itself a signal that looks like automated traffic to Shazam's endpoint, on top of
    /// the User-Agent rotation in <see cref="ShazamClient"/>.</summary>
    private static TimeSpan Jittered(TimeSpan interval)
    {
        double factor = 0.8 + Random.Shared.NextDouble() * 0.4; // [0.8, 1.2)
        return TimeSpan.FromSeconds(interval.TotalSeconds * factor);
    }

    public void Dispose()
    {
        _autoPollTimer?.Dispose();
        _capture.Dispose();
        _shazamClient.Dispose();
    }
}
