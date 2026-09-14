using AutoShazam.Models;
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
/// Downmixes any channel count to mono by averaging. NAudio's own <c>ISampleProvider.ToMono()</c>
/// only handles the mono (pass-through) and exactly-stereo cases - it throws "Source must be
/// stereo" for anything else, which loopback-capturing a speaker configured for 5.1/7.1 surround
/// hits immediately, since WASAPI loopback uses the device's own mix format.
/// </summary>
internal sealed class DownmixToMonoSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _channels;
    private float[]? _sourceBuffer;

    public DownmixToMonoSampleProvider(ISampleProvider source)
    {
        _source = source;
        _channels = source.WaveFormat.Channels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        int sourceSamplesNeeded = count * _channels;
        if (_sourceBuffer is null || _sourceBuffer.Length < sourceSamplesNeeded)
        {
            _sourceBuffer = new float[sourceSamplesNeeded];
        }

        int sourceSamplesRead = _source.Read(_sourceBuffer, 0, sourceSamplesNeeded);
        int framesRead = sourceSamplesRead / _channels;

        for (int frame = 0; frame < framesRead; frame++)
        {
            float sum = 0;
            int baseIndex = frame * _channels;
            for (int channel = 0; channel < _channels; channel++)
            {
                sum += _sourceBuffer[baseIndex + channel];
            }

            buffer[offset + frame] = sum / _channels;
        }

        return framesRead;
    }
}

/// <summary>
/// Owns a single WASAPI capture stream - either a microphone or a loopback tap on a speaker/render
/// device - continuously resampling to 16kHz mono PCM16 (the format Shazam's signature algorithm
/// expects) and exposing both a live level meter and on-demand recording snippets (for recognition).
/// </summary>
internal sealed class AudioCaptureService : IDisposable
{
    private const int TargetSampleRate = 16000;
    private const int MaxOutputFramesPerCallback = 4000; // 250ms ceiling; actual yield is bounded by available input

    private readonly object _sync = new();
    private readonly AudioDeviceService _deviceService = new();

    private AudioSourceKind _kind = AudioSourceKind.Microphone;
    private string? _deviceId;

    private WasapiCapture? _capture;
    private BufferedWaveProvider? _buffered;
    private ISampleProvider? _resampled;
    private float[]? _scratch;

    private List<short>? _recordingBuffer;
    private int _recordingTargetSamples;
    private TaskCompletionSource<short[]>? _recordingTcs;

    public event EventHandler<LevelSampleEventArgs>? LevelSample;

    /// <summary>Raised whenever capture actually starts or stops.</summary>
    public event EventHandler<bool>? ActiveChanged;

    public bool IsRunning
    {
        get { lock (_sync) { return _capture is not null; } }
    }

    /// <summary>Records what to capture next time it (re)starts, without touching a stream that's
    /// already running - the caller decides when to actually apply it via <see cref="Restart"/>.</summary>
    public void SetSource(AudioSourceKind kind, string? deviceId)
    {
        lock (_sync)
        {
            _kind = kind;
            _deviceId = deviceId;
        }
    }

    /// <summary>Starts capture if it isn't already running. Throws if the configured device is
    /// unavailable.</summary>
    public void Start()
    {
        lock (_sync)
        {
            if (_capture is not null)
            {
                return;
            }

            StartCaptureLocked();
        }
    }

    /// <summary>Stops and restarts capture against whatever source is currently configured. Throws
    /// if the new device is unavailable (capture is left stopped, not the old source).</summary>
    public void Restart()
    {
        lock (_sync)
        {
            StopCaptureLocked();
            StartCaptureLocked();
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            StopCaptureLocked();
        }
    }

    /// <summary>
    /// Records a snippet of roughly <paramref name="duration"/> of 16kHz mono PCM16 audio from
    /// whatever is currently configured. Reuses continuous capture if already running; otherwise
    /// starts and tears down a temporary capture for just this call.
    /// </summary>
    public async Task<short[]> RecordSnippetAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        TaskCompletionSource<short[]> tcs;
        bool ownsCapture;

        lock (_sync)
        {
            ownsCapture = _capture is null;
            if (ownsCapture)
            {
                StartCaptureLocked();
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

    private void StartCaptureLocked()
    {
        var kind = _kind;
        var device = _deviceService.GetDeviceById(_deviceId, kind)
            ?? throw new InvalidOperationException(kind == AudioSourceKind.Speaker
                ? "No speaker output device is available."
                : "No microphone is available.");

        WasapiCapture capture = kind == AudioSourceKind.Speaker
            ? new WasapiLoopbackCapture(device)
            : new WasapiCapture(device) { ShareMode = AudioClientShareMode.Shared };

        var buffered = new BufferedWaveProvider(capture.WaveFormat)
        {
            ReadFully = false,
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromSeconds(2),
        };

        ISampleProvider stereoOrMono = buffered.ToSampleProvider();
        ISampleProvider sampleProvider = stereoOrMono.WaveFormat.Channels switch
        {
            1 => stereoOrMono,
            2 => stereoOrMono.ToMono(),
            _ => new DownmixToMonoSampleProvider(stereoOrMono), // e.g. 5.1/7.1 surround loopback
        };
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
        // coordinator permanently "busy".
        _recordingTcs?.TrySetException(new CaptureStoppedException());

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
