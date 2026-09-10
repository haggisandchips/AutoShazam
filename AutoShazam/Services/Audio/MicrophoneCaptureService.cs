using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace AutoShazam.Services.Audio;

internal sealed class LevelSampleEventArgs : EventArgs
{
    public required double DbFs { get; init; }
    public required TimeSpan Duration { get; init; }
}

/// <summary>
/// Owns a single WASAPI capture stream for a selected microphone, continuously resampling to
/// 16kHz mono PCM16 (the format Shazam's signature algorithm expects) and exposing both a live
/// level meter (for silence detection) and on-demand recording snippets (for recognition).
/// </summary>
internal sealed class MicrophoneCaptureService : IDisposable
{
    private const int TargetSampleRate = 16000;
    private const int MaxOutputFramesPerCallback = 4000; // 250ms ceiling; actual yield is bounded by available input

    private readonly object _sync = new();
    private readonly AudioDeviceService _deviceService = new();

    private WasapiCapture? _capture;
    private BufferedWaveProvider? _buffered;
    private ISampleProvider? _resampled;
    private float[]? _scratch;

    private List<short>? _recordingBuffer;
    private int _recordingTargetSamples;
    private TaskCompletionSource<short[]>? _recordingTcs;

    public event EventHandler<LevelSampleEventArgs>? LevelSample;

    /// <summary>Raised whenever capture actually starts or stops (continuous mode, or a one-shot recording).</summary>
    public event EventHandler<bool>? ActiveChanged;

    public bool IsRunning
    {
        get { lock (_sync) { return _capture is not null; } }
    }

    public void StartContinuous(string? deviceId)
    {
        lock (_sync)
        {
            if (_capture is not null)
            {
                return;
            }
            StartCaptureLocked(deviceId);
        }
    }

    public void StopContinuous()
    {
        lock (_sync)
        {
            StopCaptureLocked();
        }
    }

    /// <summary>
    /// Records a snippet of roughly <paramref name="duration"/> of 16kHz mono PCM16 audio.
    /// Reuses continuous capture if already running; otherwise starts and tears down a
    /// temporary capture for just this call.
    /// </summary>
    public async Task<short[]> RecordSnippetAsync(string? deviceId, TimeSpan duration, CancellationToken cancellationToken)
    {
        TaskCompletionSource<short[]> tcs;
        bool ownsCapture;

        lock (_sync)
        {
            ownsCapture = _capture is null;
            if (ownsCapture)
            {
                StartCaptureLocked(deviceId);
            }

            _recordingTargetSamples = (int)(duration.TotalSeconds * TargetSampleRate);
            _recordingBuffer = new List<short>(_recordingTargetSamples + TargetSampleRate);
            tcs = new TaskCompletionSource<short[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            _recordingTcs = tcs;
        }

        using var registration = cancellationToken.Register(static state =>
        {
            ((TaskCompletionSource<short[]>)state!).TrySetCanceled();
        }, tcs);

        try
        {
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            lock (_sync)
            {
                _recordingBuffer = null;
                _recordingTcs = null;
                if (ownsCapture)
                {
                    StopCaptureLocked();
                }
            }
        }
    }

    private void StartCaptureLocked(string? deviceId)
    {
        var device = _deviceService.GetDeviceById(deviceId)
            ?? throw new InvalidOperationException("No microphone is available.");

        var capture = new WasapiCapture(device) { ShareMode = AudioClientShareMode.Shared };

        var buffered = new BufferedWaveProvider(capture.WaveFormat)
        {
            ReadFully = false,
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromSeconds(2),
        };

        ISampleProvider sampleProvider = buffered.ToSampleProvider().ToMono();
        var resampled = sampleProvider.WaveFormat.SampleRate == TargetSampleRate
            ? sampleProvider
            : new WdlResamplingSampleProvider(sampleProvider, TargetSampleRate);

        capture.DataAvailable += OnDataAvailable;

        _capture = capture;
        _buffered = buffered;
        _resampled = resampled;
        _scratch = new float[MaxOutputFramesPerCallback];

        capture.StartRecording();
        ActiveChanged?.Invoke(this, true);
    }

    private void StopCaptureLocked()
    {
        if (_capture is null)
        {
            return;
        }

        _capture.DataAvailable -= OnDataAvailable;
        try
        {
            _capture.StopRecording();
        }
        catch
        {
            // best-effort stop
        }

        _capture.Dispose();
        _capture = null;
        _buffered = null;
        _resampled = null;
        _scratch = null;

        // A recording in progress will never reach its target sample count now that capture has
        // stopped - without this, RecordSnippetAsync's awaiter would hang forever, leaving the
        // coordinator permanently "busy" (and e.g. the Shazam button permanently disabled).
        _recordingTcs?.TrySetException(new MicrophoneStoppedException());

        ActiveChanged?.Invoke(this, false);
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        BufferedWaveProvider? buffered;
        ISampleProvider? resampled;
        float[]? scratch;

        lock (_sync)
        {
            buffered = _buffered;
            resampled = _resampled;
            scratch = _scratch;
        }

        if (buffered is null || resampled is null || scratch is null || e.BytesRecorded == 0)
        {
            return;
        }

        buffered.AddSamples(e.Buffer, 0, e.BytesRecorded);

        int framesRead = resampled.Read(scratch, 0, scratch.Length);
        if (framesRead <= 0)
        {
            return;
        }

        var samples = new short[framesRead];
        double sumSquares = 0;
        for (int i = 0; i < framesRead; i++)
        {
            float f = Math.Clamp(scratch[i], -1f, 1f);
            samples[i] = (short)(f * short.MaxValue);
            sumSquares += (double)f * f;
        }

        double rms = Math.Sqrt(sumSquares / framesRead);
        double dbFs = rms > 0 ? 20 * Math.Log10(rms) : -100;
        var duration = TimeSpan.FromSeconds(framesRead / (double)TargetSampleRate);
        LevelSample?.Invoke(this, new LevelSampleEventArgs { DbFs = dbFs, Duration = duration });

        lock (_sync)
        {
            if (_recordingBuffer is not null && _recordingTcs is { Task.IsCompleted: false })
            {
                _recordingBuffer.AddRange(samples);
                if (_recordingBuffer.Count >= _recordingTargetSamples)
                {
                    _recordingTcs.TrySetResult(_recordingBuffer.ToArray());
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            StopCaptureLocked();
        }
    }
}
