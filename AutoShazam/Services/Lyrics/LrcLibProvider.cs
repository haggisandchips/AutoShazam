using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AutoShazam.Models;

namespace AutoShazam.Services.Lyrics;

/// <summary>
/// Free, keyless lyrics API (lrclib.net) built specifically for synced (LRC, line-timestamped)
/// lyrics - the only provider here that can return <see cref="LyricsResult.SyncedLines"/>, so it's
/// tried first. Matches by artist/title search rather than the exact-match endpoint, since Shazam's
/// metadata (no album/duration) doesn't reliably line up with LRCLIB's exact-match lookup.
/// </summary>
internal sealed partial class LrcLibProvider : ILyricsProvider, IDisposable
{
    private readonly HttpClient _http;

    public LrcLibProvider()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "AutoShazam/1.0 (+https://github.com/haggisandchips/AutoShazam)");
    }

    public async Task<LyricsResult?> GetLyricsAsync(string artist, string title, CancellationToken cancellationToken)
    {
        try
        {
            var url = "https://lrclib.net/api/search"
                + $"?track_name={Uri.EscapeDataString(title)}&artist_name={Uri.EscapeDataString(artist)}";
            using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var results = await response.Content
                .ReadFromJsonAsync<List<LrcLibTrack>>(cancellationToken)
                .ConfigureAwait(false);

            var best = results?.FirstOrDefault(r => string.Equals(r.ArtistName, artist, StringComparison.OrdinalIgnoreCase))
                ?? results?.FirstOrDefault();

            if (best is null || best.Instrumental)
            {
                return null;
            }

            var synced = ParseSyncedLyrics(best.SyncedLyrics);
            var plain = string.IsNullOrWhiteSpace(best.PlainLyrics) ? null : best.PlainLyrics!.Trim();

            return synced is null && plain is null ? null : new LyricsResult(plain, synced);
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<LyricLine>? ParseSyncedLyrics(string? lrc)
    {
        if (string.IsNullOrWhiteSpace(lrc))
        {
            return null;
        }

        var lines = new List<LyricLine>();
        foreach (var rawLine in lrc.Split('\n'))
        {
            var match = TimestampRegex().Match(rawLine);
            if (!match.Success)
            {
                continue;
            }

            int minutes = int.Parse(match.Groups[1].Value);
            int seconds = int.Parse(match.Groups[2].Value);
            int fraction = int.Parse(match.Groups[3].Value.PadRight(3, '0'));
            var text = rawLine[match.Length..].Trim();

            if (text.Length > 0)
            {
                lines.Add(new LyricLine(new TimeSpan(0, 0, minutes, seconds, fraction), text));
            }
        }

        return lines.Count > 0 ? lines : null;
    }

    [GeneratedRegex(@"^\[(\d{2}):(\d{2})\.(\d{2,3})\]")]
    private static partial Regex TimestampRegex();

    public void Dispose() => _http.Dispose();

    private sealed class LrcLibTrack
    {
        [JsonPropertyName("artistName")]
        public string? ArtistName { get; set; }

        [JsonPropertyName("instrumental")]
        public bool Instrumental { get; set; }

        [JsonPropertyName("plainLyrics")]
        public string? PlainLyrics { get; set; }

        [JsonPropertyName("syncedLyrics")]
        public string? SyncedLyrics { get; set; }
    }
}
