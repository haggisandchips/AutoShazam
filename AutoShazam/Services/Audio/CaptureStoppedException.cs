namespace AutoShazam.Services.Audio;

/// <summary>
/// Thrown to unblock a pending <see cref="AudioCaptureService.RecordSnippetAsync"/> call when
/// capture is torn down before it finished recording (e.g. the source was switched mid-attempt).
/// Deliberately NOT an <see cref="OperationCanceledException"/> - that type is also what
/// HttpClient throws on its own request timeout, and conflating the two would let a genuine
/// network failure be silently swallowed as "just a routine capture teardown."
/// </summary>
internal sealed class CaptureStoppedException : Exception
{
    public CaptureStoppedException()
        : base("Audio capture stopped before the recording finished.")
    {
    }
}
