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

/// <summary>A recorded clip together with the wall-clock time its first sample was actually
/// captured at - needed (rather than just "whenever the caller happened to ask for it") because a
/// clip served from the rolling buffer already started up to <see cref="RecordedClip"/>'s duration
/// in the past by the time it's handed back, and lyrics sync needs to know exactly when the audio
/// Shazam matched against was really captured relative to the match's reported track offset.</summary>
internal readonly record struct RecordedClip(short[] Samples, DateTime StartedUtc);

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

    // While capture is running, every resampled sample is also kept here - a fixed-size window of
    // the most recent audio, always a few seconds longer than any clip we actually ask for. This
    // lets RecordSnippetAsync serve a request instantly once enough has accumulated, by slicing the
    // trailing window out of already-captured audio, instead of waiting for a whole fresh clip to
    // be recorded from scratch every time - letting Auto Shazam re-check far more often than the
    // clip length itself without querying Shazam on genuinely fresh audio any less often.
    private static readonly TimeSpan RollingBufferCapacity = TimeSpan.FromSeconds(12);

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

    private short[]? _rollingBuffer;
    private int _rollingWritePos;
    private int _rollingFilledCount;
    private DateTime _rollingBufferLastWriteUtc;

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
    public async Task<RecordedClip> RecordSnippetAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        // Fast path: continuous capture has already been running long enough to have this whole
        // window buffered, so hand back a slice of it immediately. Only misses on the very first
        // attempt after (re)starting capture (or a manual click while Auto Shazam is off, which
        // isn't capturing continuously at all) - those fall through to actually recording below.
        var rolling = TryGetRollingSnippet(duration);
        if (rolling is not null)
        {
            return rolling.Value;
        }

        TaskCompletionSource<short[]> tcs;
        bool ownsCapture;
        DateTime startedUtc;

        lock (_sync)
        {
            ownsCapture = _capture is null;
            if (ownsCapture)
            {
                StartCaptureLocked();
            }

            startedUtc = DateTime.UtcNow;
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
            var samples = await tcs.Task.ConfigureAwait(false);
            return new RecordedClip(samples, startedUtc);
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
        _rollingBuffer = new short[(int)(RollingBufferCapacity.TotalSeconds * TargetSampleRate)];
        _rollingWritePos = 0;
        _rollingFilledCount = 0;

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
        _rollingBuffer = null;
        _rollingWritePos = 0;
        _rollingFilledCount = 0;

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

            WriteToRollingBufferLocked(samples);
        }
    }

    /// <summary>Appends to the rolling buffer, wrapping around and overwriting the oldest audio
    /// once full. Must be called with <see cref="_sync"/> already held.</summary>
    private void WriteToRollingBufferLocked(short[] samples)
    {
        if (_rollingBuffer is null)
        {
            return;
        }

        int remaining = samples.Length;
        int sourceOffset = 0;
        while (remaining > 0)
        {
            int spaceToEnd = _rollingBuffer.Length - _rollingWritePos;
            int chunk = Math.Min(remaining, spaceToEnd);
            Array.Copy(samples, sourceOffset, _rollingBuffer, _rollingWritePos, chunk);
            _rollingWritePos = (_rollingWritePos + chunk) % _rollingBuffer.Length;
            sourceOffset += chunk;
            remaining -= chunk;
        }

        _rollingFilledCount = Math.Min(_rollingFilledCount + samples.Length, _rollingBuffer.Length);
        _rollingBufferLastWriteUtc = DateTime.UtcNow;
    }

    /// <summary>Returns the trailing <paramref name="duration"/> of already-captured audio, or null
    /// if capture isn't running or hasn't been running long enough yet to have that much buffered.
    /// The returned clip's StartedUtc is backdated from the most recent write, since by definition
    /// this audio was captured before now, not starting now.</summary>
    private RecordedClip? TryGetRollingSnippet(TimeSpan duration)
    {
        lock (_sync)
        {
            if (_rollingBuffer is null)
            {
                return null;
            }

            int needed = (int)(duration.TotalSeconds * TargetSampleRate);
            if (needed > _rollingBuffer.Length || _rollingFilledCount < needed)
            {
                return null;
            }

            var result = new short[needed];
            int startPos = (_rollingWritePos - needed + _rollingBuffer.Length) % _rollingBuffer.Length;
            int firstChunk = Math.Min(needed, _rollingBuffer.Length - startPos);
            Array.Copy(_rollingBuffer, startPos, result, 0, firstChunk);
            if (firstChunk < needed)
            {
                Array.Copy(_rollingBuffer, 0, result, firstChunk, needed - firstChunk);
            }

            return new RecordedClip(result, _rollingBufferLastWriteUtc - duration);
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
