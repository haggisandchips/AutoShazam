using System.Net.Http;
using System.Net.Http.Json;
using AutoShazam.Services.Diagnostics;

namespace AutoShazam.Services.Shazam;

internal sealed class ShazamMatchResult
{
    public required string Title { get; init; }
    public required string Artist { get; init; }
    public string? CoverArtUrl { get; init; }

    /// <summary>Seconds into the track where the matched clip was taken (0 if Shazam didn't report
    /// one), so the caller can estimate ongoing playback position for lyrics sync.</summary>
    public double OffsetSeconds { get; init; }
}

/// <summary>
/// Minimal client for Shazam's (undocumented) mobile-app recognition endpoint. Builds a binary
/// audio signature from raw PCM samples via <see cref="SignatureGenerator"/> and posts it the
/// same way Shazam's own iPhone app does.
/// </summary>
internal sealed class ShazamClient : IDisposable
{
    private const string Lang = "en";
    private const string Region = "US";
    private const double MaxSignatureSeconds = 8;

    private readonly HttpClient _http;
    private readonly RecognitionLog _log;

    public ShazamClient(RecognitionLog log)
    {
        _log = log;
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
        _http.DefaultRequestHeaders.Add("X-Shazam-Platform", "IPHONE");
        _http.DefaultRequestHeaders.Add("X-Shazam-AppVersion", "14.1.0");
        _http.DefaultRequestHeaders.Add("Accept", "*/*");
        _http.DefaultRequestHeaders.Add("Accept-Language", Lang);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Shazam/3685 CFNetwork/1197 Darwin/20.0.0");
    }

    public async Task<ShazamMatchResult?> RecognizeAsync(short[] pcm16kHzMonoSamples, CancellationToken cancellationToken)
    {
        var generator = new SignatureGenerator { MaxTimeSeconds = MaxSignatureSeconds };
        generator.FeedInput(pcm16kHzMonoSamples);
        var signature = generator.GetNextSignature();
        if (signature is null || signature.NumberSamples == 0)
        {
            _log.Write($"Recognize: captured {pcm16kHzMonoSamples.Length} raw samples but produced no signature - nothing sent to Shazam.");
            return null;
        }

        int totalPeaks = signature.FrequencyBandToSoundPeaks.Values.Sum(peaks => peaks.Count);
        _log.Write(
            $"Recognize: signature covers {signature.NumberSamples / (double)signature.SampleRateHz:F1}s "
            + $"({signature.NumberSamples} samples), {totalPeaks} fingerprint peaks across "
            + $"{signature.FrequencyBandToSoundPeaks.Count} frequency bands.");

        var uuidA = Guid.NewGuid().ToString().ToUpperInvariant();
        var uuidB = Guid.NewGuid().ToString().ToUpperInvariant();
        var url = $"https://amp.shazam.com/discovery/v5/{Lang}/{Region}/iphone/-/tag/{uuidA}/{uuidB}"
                  + "?sync=true&webv3=true&sampling=true&connected=&shazamapiversion=v3&sharehub=true&hubv5minorversion=v5.1&hidelb=true&video=v3";

        var payload = new
        {
            timezone = GetIanaTimeZoneId(),
            signature = new
            {
                uri = signature.EncodeToUri(),
                samplems = (int)(signature.NumberSamples / (double)signature.SampleRateHz * 1000),
            },
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            context = new { },
            geolocation = new { },
        };

        using var response = await _http.PostAsJsonAsync(url, payload, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var result = await response.Content
            .ReadFromJsonAsync<ShazamRecognizeResponse>(cancellationToken)
            .ConfigureAwait(false);

        if (result?.Track is null)
        {
            _log.Write($"Recognize: Shazam returned no track (matches={result?.Matches?.Count ?? 0}).");
            return null;
        }

        _log.Write($"Recognize: matched '{result.Track.Title}' by '{result.Track.Subtitle}'.");

        var coverArt = result.Track.Images?.CoverArtHq ?? result.Track.Images?.CoverArt;

        return new ShazamMatchResult
        {
            Title = result.Track.Title ?? "Unknown title",
            Artist = result.Track.Subtitle ?? "Unknown artist",
            CoverArtUrl = coverArt,
            OffsetSeconds = result.Matches?.FirstOrDefault()?.Offset ?? 0,
        };
    }

    private static string GetIanaTimeZoneId()
    {
        try
        {
            if (TimeZoneInfo.TryConvertWindowsIdToIanaId(TimeZoneInfo.Local.Id, out var iana) && iana is not null)
            {
                return iana;
            }
        }
        catch
        {
            // fall through to UTC
        }

        return "UTC";
    }

    public void Dispose() => _http.Dispose();
}
