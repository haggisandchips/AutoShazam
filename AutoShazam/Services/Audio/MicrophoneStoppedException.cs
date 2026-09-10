namespace AutoShazam.Services.Audio;

/// <summary>
/// Thrown to unblock a pending <see cref="MicrophoneCaptureService.RecordSnippetAsync"/> call when
/// capture is torn down before it finished recording (e.g. auto mode switched off mid-attempt).
/// Deliberately NOT an <see cref="OperationCanceledException"/> - that type is also what
/// HttpClient throws on its own request timeout, and conflating the two would let a genuine
/// network failure be silently swallowed as "just a routine mic teardown."
/// </summary>
internal sealed class MicrophoneStoppedException : Exception
{
    public MicrophoneStoppedException()
        : base("Microphone capture stopped before the recording finished.")
    {
    }
}
