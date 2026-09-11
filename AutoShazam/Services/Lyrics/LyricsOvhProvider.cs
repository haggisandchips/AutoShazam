using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace AutoShazam.Services.Lyrics;

/// <summary>Free, keyless public lyrics API - no auth, but a smaller catalog than the licensed
/// providers, so this is used as the last-resort fallback.</summary>
internal sealed class LyricsOvhProvider : ILyricsProvider, IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public async Task<LyricsResult?> GetLyricsAsync(string artist, string title, CancellationToken cancellationToken)
    {
        try
        {
            var url = $"https://api.lyrics.ovh/v1/{Uri.EscapeDataString(artist)}/{Uri.EscapeDataString(title)}";
            using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var result = await response.Content
                .ReadFromJsonAsync<LyricsOvhResponse>(cancellationToken)
                .ConfigureAwait(false);

            var lyrics = result?.Lyrics?.Trim();
            return string.IsNullOrWhiteSpace(lyrics) ? null : new LyricsResult(lyrics, null);
        }
        catch
        {
            return null;
        }
    }

    public void Dispose() => _http.Dispose();

    private sealed class LyricsOvhResponse
    {
        [JsonPropertyName("lyrics")]
        public string? Lyrics { get; set; }
    }
}
