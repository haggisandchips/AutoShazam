using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace AutoShazam.Services.Lyrics;

/// <summary>
/// Looks up lyrics via Musixmatch's undocumented desktop-app endpoint - the same "usertoken"
/// handshake Musixmatch's own desktop/web clients use. There is no public Musixmatch API that
/// returns full lyrics without a commercial license, so this mirrors the reverse-engineering
/// approach already used for Shazam recognition elsewhere in this app (see
/// <see cref="AutoShazam.Services.Shazam.ShazamClient"/>). Best-effort: if Musixmatch blocks,
/// rate-limits, or changes this endpoint, calls here just return null and the next provider in
/// <see cref="LyricsService"/>'s chain is tried.
/// </summary>
internal sealed class MusixmatchProvider : ILyricsProvider, IDisposable
{
    private const string AppId = "web-desktop-app-v1.0";
    private const string PromoMarker = "******* This Lyrics is NOT for Commercial use";

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _userToken;

    public MusixmatchProvider()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
    }

    public async Task<string?> GetLyricsAsync(string artist, string title, CancellationToken cancellationToken)
    {
        try
        {
            string? token = await GetUserTokenAsync(cancellationToken).ConfigureAwait(false);
            if (token is null)
            {
                return null;
            }

            var url = "https://apic-desktop.musixmatch.com/ws/1.1/matcher.lyrics.get"
                + $"?q_track={Uri.EscapeDataString(title)}&q_artist={Uri.EscapeDataString(artist)}"
                + $"&usertoken={Uri.EscapeDataString(token)}&app_id={AppId}&format=json";

            using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var result = await response.Content
                .ReadFromJsonAsync<MusixmatchLyricsResponse>(cancellationToken)
                .ConfigureAwait(false);

            if (result?.Message?.Header?.StatusCode != 200)
            {
                return null;
            }

            var body = result.Message.Body?.Lyrics?.LyricsBody;
            if (string.IsNullOrWhiteSpace(body))
            {
                return null;
            }

            // The plain-text body carries a trailing non-commercial-use promo line - strip it.
            int marker = body.IndexOf(PromoMarker, StringComparison.OrdinalIgnoreCase);
            if (marker >= 0)
            {
                body = body[..marker].TrimEnd();
            }

            return string.IsNullOrWhiteSpace(body) ? null : body;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Musixmatch's token is a session handshake, not a per-track credential - fetched
    /// once and reused for the life of the app rather than per lookup.</summary>
    private async Task<string?> GetUserTokenAsync(CancellationToken cancellationToken)
    {
        if (_userToken is not null)
        {
            return _userToken;
        }

        await _tokenLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_userToken is not null)
            {
                return _userToken;
            }

            var url = $"https://apic-desktop.musixmatch.com/ws/1.1/token.get?app_id={AppId}";
            using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var result = await response.Content
                .ReadFromJsonAsync<MusixmatchTokenResponse>(cancellationToken)
                .ConfigureAwait(false);

            if (result?.Message?.Header?.StatusCode != 200)
            {
                return null;
            }

            _userToken = result.Message.Body?.UserToken;
            return _userToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _tokenLock.Dispose();
    }

    private sealed class MusixmatchHeader
    {
        [JsonPropertyName("status_code")]
        public int StatusCode { get; set; }
    }

    private sealed class MusixmatchTokenResponse
    {
        [JsonPropertyName("message")]
        public MusixmatchTokenMessage? Message { get; set; }
    }

    private sealed class MusixmatchTokenMessage
    {
        [JsonPropertyName("header")]
        public MusixmatchHeader? Header { get; set; }

        [JsonPropertyName("body")]
        public MusixmatchTokenBody? Body { get; set; }
    }

    private sealed class MusixmatchTokenBody
    {
        [JsonPropertyName("user_token")]
        public string? UserToken { get; set; }
    }

    private sealed class MusixmatchLyricsResponse
    {
        [JsonPropertyName("message")]
        public MusixmatchLyricsMessage? Message { get; set; }
    }

    private sealed class MusixmatchLyricsMessage
    {
        [JsonPropertyName("header")]
        public MusixmatchHeader? Header { get; set; }

        [JsonPropertyName("body")]
        public MusixmatchLyricsBody? Body { get; set; }
    }

    private sealed class MusixmatchLyricsBody
    {
        [JsonPropertyName("lyrics")]
        public MusixmatchLyrics? Lyrics { get; set; }
    }

    private sealed class MusixmatchLyrics
    {
        [JsonPropertyName("lyrics_body")]
        public string? LyricsBody { get; set; }
    }
}
