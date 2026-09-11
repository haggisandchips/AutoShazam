namespace AutoShazam.Services.Lyrics;

/// <summary>One lyrics source, tried in turn by <see cref="LyricsService"/>. Implementations are
/// best-effort - any failure (not found, blocked, rate-limited, endpoint changed) should be
/// swallowed and reported as null rather than thrown, so one provider going down just falls
/// through to the next.</summary>
internal interface ILyricsProvider
{
    Task<LyricsResult?> GetLyricsAsync(string artist, string title, CancellationToken cancellationToken);
}
