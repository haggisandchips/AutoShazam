namespace AutoShazam.Models;

/// <param name="MatchOffsetSeconds">Seconds into the track where the matched clip was taken (0 if
/// unknown).</param>
/// <param name="RecordingStartedUtc">When the microphone clip that produced this match started
/// recording - together with <paramref name="MatchOffsetSeconds"/>, lets a listener estimate the
/// track's current playback position for lyrics sync.</param>
public sealed record RecognitionResult(
    string Title,
    string Artist,
    string? CoverArtUrl,
    double MatchOffsetSeconds,
    DateTime RecordingStartedUtc);
