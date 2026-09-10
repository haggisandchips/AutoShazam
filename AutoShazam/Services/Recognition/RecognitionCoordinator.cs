using AutoShazam.Models;
using AutoShazam.Services.Audio;
using AutoShazam.Services.Diagnostics;
using AutoShazam.Services.Shazam;

namespace AutoShazam.Services.Recognition;

/// <summary>
/// Orchestrates recognition attempts (manual button clicks, silence-gap triggers, and a periodic
/// fallback) through a single gate, so Shazam is never queried concurrently or in a tight loop:
/// - Only one recognition runs at a time.
/// - Auto-triggered recognitions respect a cooldown so a run of short gaps - or the periodic
///   fallback timer - can't fire off a burst of requests. The cooldown is shorter after a miss
///   (worth trying again soon) than after a match (probably still the same track playing).
/// - A silence-gap that arrives while a recognition is already running isn't dropped: it's
///   remembered and immediately retried (bypassing the cooldown) once the current attempt
///   finishes, since a gap is strong evidence the track actually changed.
/// - A periodic timer provides a fallback trigger for sources with no clean silence between
///   tracks (crossfaded/gapless streaming, DJ mixes, live albums) where silence-gap detection
///   alone would never fire again after the first attempt.
/// - A manual button click always attempts to run (subject only to the busy guard), and by
///   construction always performs exactly one recognition.
/// </summary>
internal sealed class RecognitionCoordinator : IDisposable
{
    private static readonly TimeSpan RecognitionClipDuration = TimeSpan.FromSeconds(9);
    private static readonly TimeSpan AutoCooldownAfterMatch = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan AutoCooldownAfterMiss = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan AutoRetryPollInterval = TimeSpan.FromSeconds(5);

    private readonly MicrophoneCaptureService _microphone = new();
    private readonly SilenceGapDetector _gapDetector = new();
    private readonly RecognitionLog _log;
    private readonly ShazamClient _shazamClient;

    private readonly object _sync = new();
    private bool _busy;
    private bool _pendingGapRetry;
    private DateTime _lastRecognitionUtc = DateTime.MinValue;
    private TimeSpan _currentAutoCooldown = AutoCooldownAfterMiss;
    private bool _autoEnabled;
    private string? _deviceId;
    private CancellationTokenSource? _autoSessionCts;
    private Timer? _autoRetryTimer;

    public event EventHandler? RecognitionStarted;
    public event EventHandler<RecognitionResult>? RecognitionSucceeded;
    public event EventHandler? RecognitionNoMatch;
    public event EventHandler<string>? RecognitionFailed;

    /// <summary>Raised instead of any of the above when an in-flight attempt was cut short by the
    /// microphone being torn down (e.g. auto mode turned off mid-recording) rather than actually
    /// completing. No message - just a signal that whoever set busy/listening state should clear it.</summary>
    public event EventHandler? RecognitionCancelled;
    public event EventHandler<bool>? MicrophoneActiveChanged;

    /// <summary>The current microphone level in dBFS, for level-meter UI. Only meaningful while
    /// the microphone is active.</summary>
    public event EventHandler<double>? LevelChanged;

    /// <summary>Raised immediately before/after the actual HTTP call to Shazam (a subset of a
    /// full recognition attempt, which also spends time recording audio first).</summary>
    public event EventHandler<bool>? ShazamQueryActiveChanged;

    /// <summary>Raised when auto mode has been listening to nothing but silence for too long. The
    /// caller (view model) is expected to actually turn Auto Shazam off in response, since that's
    /// also what's bound to the title bar toggle.</summary>
    public event EventHandler? AutoDisabledBySilence;

    /// <summary>Raised when auto mode was already running and had to stop because the microphone
    /// failed mid-session (e.g. the device was switched to one that's unavailable). Not raised for
    /// the initial <see cref="SetAutoEnabled"/> failure - that one is thrown back to the caller
    /// synchronously so the toggle never has to be told "actually, no" after the fact.</summary>
    public event EventHandler<string>? AutoStoppedUnexpectedly;

    public RecognitionCoordinator(string appDataRoot)
    {
        _log = new RecognitionLog(appDataRoot);
        _shazamClient = new ShazamClient(_log);

        _gapDetector.GapEnded += (_, _) => _ = TryStartRecognitionAsync(auto: true, bypassCooldown: false);
        _gapDetector.ExtendedSilenceDetected += (_, _) =>
        {
            if (_autoEnabled)
            {
                AutoDisabledBySilence?.Invoke(this, EventArgs.Empty);
            }
        };
        _microphone.LevelSample += (_, e) =>
        {
            _gapDetector.OnLevelSample(e.DbFs, e.Duration);
            LevelChanged?.Invoke(this, e.DbFs);
        };
        _microphone.ActiveChanged += (_, active) => MicrophoneActiveChanged?.Invoke(this, active);
    }

    public void SetExtendedSilenceTimeout(TimeSpan timeout) => _gapDetector.ExtendedSilenceThreshold = timeout;

    public void SetSilenceThreshold(double thresholdDbFs) => _gapDetector.SilenceThresholdDbFs = thresholdDbFs;

    public void SetDevice(string? deviceId)
    {
        lock (_sync)
        {
            _deviceId = deviceId;
        }

        if (!_autoEnabled)
        {
            return;
        }

        try
        {
            _microphone.StopContinuous();
            _microphone.StartContinuous(deviceId);
            _gapDetector.Reset();
        }
        catch (Exception ex)
        {
            // The old device stopped and the new one didn't come up - don't leave auto mode
            // silently "on" with a dead microphone.
            StopAutoState();
            AutoStoppedUnexpectedly?.Invoke(this, ex.Message);
        }
    }

    /// <summary>
    /// Enables or disables auto mode. Enabling performs an immediate initial check and starts the
    /// periodic fallback timer. If the microphone fails to start, auto mode is left off and the
    /// exception propagates to the caller (rather than leaving auto mode looking "on" with nothing
    /// running).
    /// </summary>
    public void SetAutoEnabled(bool enabled)
    {
        if (_autoEnabled == enabled)
        {
            return;
        }

        if (enabled)
        {
            _autoSessionCts = new CancellationTokenSource();
            _gapDetector.Reset();
            _microphone.StartContinuous(_deviceId); // throws on failure; _autoEnabled stays false
            _autoEnabled = true;
            _currentAutoCooldown = AutoCooldownAfterMiss;

            _autoRetryTimer = new Timer(
                _ => _ = TryStartRecognitionAsync(auto: true, bypassCooldown: false),
                null,
                AutoRetryPollInterval,
                AutoRetryPollInterval);

            _ = TryStartRecognitionAsync(auto: false, bypassCooldown: true); // initial check
        }
        else
        {
            StopAutoState();
        }
    }

    private void StopAutoState()
    {
        _autoEnabled = false;
        _pendingGapRetry = false;
        _autoRetryTimer?.Dispose();
        _autoRetryTimer = null;
        _autoSessionCts?.Cancel();
        _autoSessionCts?.Dispose();
        _autoSessionCts = null;
        _microphone.StopContinuous();
        _gapDetector.Reset();
    }

    /// <summary>Manual one-shot recognition (works regardless of auto mode).</summary>
    public Task TriggerManualAsync() => TryStartRecognitionAsync(auto: false, bypassCooldown: true);

    private async Task TryStartRecognitionAsync(bool auto, bool bypassCooldown)
    {
        lock (_sync)
        {
            if (_busy)
            {
                if (auto)
                {
                    // A transition was detected (or the fallback timer fired) while we were
                    // already mid-recognition. Don't lose that signal - retry the moment the
                    // current attempt finishes, bypassing the cooldown, since this is good
                    // evidence the track just changed.
                    _pendingGapRetry = true;
                }
                return;
            }

            if (auto && !bypassCooldown && DateTime.UtcNow - _lastRecognitionUtc < _currentAutoCooldown)
            {
                return;
            }

            _busy = true;
            _pendingGapRetry = false;
        }

        RecognitionStarted?.Invoke(this, EventArgs.Empty);
        _log.Write($"Attempt started (auto={auto}, bypassCooldown={bypassCooldown}).");

        try
        {
            var deviceId = _deviceId;
            var samples = await _microphone.RecordSnippetAsync(deviceId, RecognitionClipDuration, CancellationToken.None)
                .ConfigureAwait(false);

            ShazamQueryActiveChanged?.Invoke(this, true);
            ShazamMatchResult? match;
            try
            {
                match = await _shazamClient.RecognizeAsync(samples, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                ShazamQueryActiveChanged?.Invoke(this, false);
            }

            if (match is null)
            {
                _currentAutoCooldown = AutoCooldownAfterMiss;
                _log.Write("Attempt outcome: no match.");
                RecognitionNoMatch?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                _currentAutoCooldown = AutoCooldownAfterMatch;
                _log.Write($"Attempt outcome: matched '{match.Title}' by '{match.Artist}'.");
                RecognitionSucceeded?.Invoke(this, new RecognitionResult(match.Title, match.Artist, match.CoverArtUrl));
            }
        }
        catch (MicrophoneStoppedException)
        {
            // The microphone was torn down mid-recording (e.g. auto mode was switched off while
            // this attempt was in flight) - not a real failure, nothing to report. Deliberately
            // NOT catching the broader OperationCanceledException here: HttpClient throws that
            // same base type on its own request timeout, and a genuine network failure should be
            // surfaced via RecognitionFailed below, not silently swallowed as routine teardown.
            _currentAutoCooldown = AutoCooldownAfterMiss;
            _log.Write("Attempt outcome: cancelled (microphone stopped mid-recording).");
            RecognitionCancelled?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _currentAutoCooldown = AutoCooldownAfterMiss;
            _log.Write($"Attempt outcome: failed - {ex}");
            RecognitionFailed?.Invoke(this, ex.Message);
        }
        finally
        {
            bool retryPending;
            lock (_sync)
            {
                _busy = false;
                _lastRecognitionUtc = DateTime.UtcNow;
                retryPending = _pendingGapRetry;
                _pendingGapRetry = false;
            }

            if (retryPending && _autoEnabled)
            {
                _ = TryStartRecognitionAsync(auto: true, bypassCooldown: true);
            }
        }
    }

    public void Dispose()
    {
        _autoRetryTimer?.Dispose();
        _autoSessionCts?.Cancel();
        _autoSessionCts?.Dispose();
        _microphone.Dispose();
        _shazamClient.Dispose();
    }
}
