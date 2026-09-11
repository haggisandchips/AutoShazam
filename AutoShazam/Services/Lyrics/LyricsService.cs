namespace AutoShazam.Services.Lyrics;

/// <summary>
/// Looks up lyrics by trying each source in <see cref="_providers"/> in order until one returns
/// something. Both hits and misses are cached in memory for the lifetime of the app, keyed by
/// artist+title, so repeat recognitions of the same track (e.g. Auto Shazam re-triggering) don't
/// re-query every provider.
/// </summary>
internal sealed class LyricsService : IDisposable
{
    private readonly IReadOnlyList<ILyricsProvider> _providers;
    private readonly Dictionary<string, string?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();

    public LyricsService()
        : this(new MusixmatchProvider(), new LyricsOvhProvider())
    {
    }

    internal LyricsService(params ILyricsProvider[] providers)
    {
        _providers = providers;
    }

    public async Task<string?> GetLyricsAsync(string artist, string title, CancellationToken cancellationToken = default)
    {
        string key = $"{artist}|{title}";

        lock (_sync)
        {
            if (_cache.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }

        string? lyrics = null;
        foreach (var provider in _providers)
        {
            lyrics = await provider.GetLyricsAsync(artist, title, cancellationToken).ConfigureAwait(false);
            if (lyrics is not null)
            {
                break;
            }
        }

        lock (_sync)
        {
            _cache[key] = lyrics;
        }

        return lyrics;
    }

    public void Dispose()
    {
        foreach (var provider in _providers)
        {
            (provider as IDisposable)?.Dispose();
        }
    }
}
