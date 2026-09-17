using System.Net;
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
/// Result of one recognition call. <see cref="RateLimitedRetryAfter"/> is set whenever the
/// response signalled we're querying too fast - either an explicit 429, or (defensively, since
/// this is an undocumented endpoint) a Retry-After/rate-limit-remaining header on any response -
/// so the caller can back off even on an otherwise-successful call.
/// </summary>
internal sealed record ShazamRecognizeOutcome(ShazamMatchResult? Match, TimeSpan? RateLimitedRetryAfter);

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
    private static readonly TimeSpan DefaultRateLimitBackoff = TimeSpan.FromSeconds(30);

    // Rotated per request rather than sent once and reused - Shazam is known to quietly
    // degrade (not reject outright, just start returning empty matches for genuinely
    // fingerprinted tracks) traffic that hammers this undocumented endpoint from behind a
    // single, unchanging User-Agent for a long time, which is exactly what Auto Shazam's
    // continuous polling looks like from the server's side. Pool borrowed from shazamio, an
    // actively maintained reverse-engineered client that rotates for the same reason.
    private static readonly string[] UserAgents =
    {
        "Dalvik/2.1.0 (Linux; U; Android 5.0.2; VS980 4G Build/LRX22G)",
        "Dalvik/1.6.0 (Linux; U; Android 4.4.2; SM-T210 Build/KOT49H)",
        "Dalvik/2.1.0 (Linux; U; Android 5.1.1; SM-P905V Build/LMY47X)",
        "Dalvik/1.6.0 (Linux; U; Android 4.4.4; Vodafone Smart Tab 4G Build/KTU84P)",
        "Dalvik/1.6.0 (Linux; U; Android 4.4.4; SM-G360H Build/KTU84P)",
        "Dalvik/2.1.0 (Linux; U; Android 5.0.2; SM-S920L Build/LRX22G)",
        "Dalvik/2.1.0 (Linux; U; Android 6.0.1; SM-G920F Build/MMB29K)",
        "Dalvik/2.1.0 (Linux; U; Android 5.0; SM-N9005 Build/LRX21V)",
        "Dalvik/1.6.0 (Linux; U; Android 4.4.2; SM-G7102 Build/KOT49H)",
        "Dalvik/2.1.0 (Linux; U; Android 6.0.1; SM-G928F Build/MMB29K)",
        "Dalvik/2.1.0 (Linux; U; Android 5.1.1; SM-J500FN Build/LMY48B)",
        "Dalvik/1.6.0 (Linux; U; Android 4.4.2; GT-I9500 Build/KOT49H)",
        "Dalvik/2.1.0 (Linux; U; Android 5.1.1; SM-A310F Build/LMY47X)",
        "Dalvik/1.6.0 (Linux; U; Android 4.4.4; C6903 Build/14.4.A.0.157)",
        "Dalvik/2.1.0 (Linux; U; Android 6.0; LG-H815 Build/MRA58K)",
        "Dalvik/2.1.0 (Linux; U; Android 5.1; XT1045 Build/LPB23.13-61)",
        "Dalvik/1.6.0 (Linux; U; Android 4.4.2; SM-N7505 Build/KOT49H)",
        "Dalvik/2.1.0 (Linux; U; Android 6.0.1; SM-G930F Build/MMB29K)",
    };

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
        // Deliberately not a default header - see RecognizeAsync, which picks a fresh one per call.
    }

    private static string PickUserAgent() => UserAgents[Random.Shared.Next(UserAgents.Length)];

    public async Task<ShazamRecognizeOutcome> RecognizeAsync(short[] pcm16kHzMonoSamples, CancellationToken cancellationToken)
    {
        var generator = new SignatureGenerator { MaxTimeSeconds = MaxSignatureSeconds };
        generator.FeedInput(pcm16kHzMonoSamples);
        var signature = generator.GetNextSignature();
        if (signature is null || signature.NumberSamples == 0)
        {
            _log.Write($"Recognize: captured {pcm16kHzMonoSamples.Length} raw samples but produced no signature - nothing sent to Shazam.");
            return new ShazamRecognizeOutcome(null, null);
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

        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(payload) };
        request.Headers.UserAgent.ParseAdd(PickUserAgent());

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

        var rateLimit = TryGetRateLimitRetryAfter(response);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = rateLimit ?? DefaultRateLimitBackoff;
            _log.Write($"Recognize: rate limited by Shazam (429) - backing off {retryAfter.TotalSeconds:F0}s.");
            return new ShazamRecognizeOutcome(null, retryAfter);
        }

        response.EnsureSuccessStatusCode();

        var result = await response.Content
            .ReadFromJsonAsync<ShazamRecognizeResponse>(cancellationToken)
            .ConfigureAwait(false);

        if (result?.Track is null)
        {
            _log.Write($"Recognize: Shazam returned no track (matches={result?.Matches?.Count ?? 0}).");
            return new ShazamRecognizeOutcome(null, rateLimit);
        }

        _log.Write($"Recognize: matched '{result.Track.Title}' by '{result.Track.Subtitle}'.");

        var coverArt = result.Track.Images?.CoverArtHq ?? result.Track.Images?.CoverArt;

        var match = new ShazamMatchResult
        {
            Title = result.Track.Title ?? "Unknown title",
            Artist = result.Track.Subtitle ?? "Unknown artist",
            CoverArtUrl = coverArt,
            OffsetSeconds = result.Matches?.FirstOrDefault()?.Offset ?? 0,
        };

        return new ShazamRecognizeOutcome(match, rateLimit);
    }

    /// <summary>
    /// Looks for a reason to back off even on a response that isn't a hard 429 - a Retry-After
    /// header, or a generic X-RateLimit-Remaining-style header reporting we're out of budget.
    /// Shazam's endpoint is undocumented and unofficial, so this is defensive: honor whatever
    /// signal it happens to send rather than assuming only 429 ever means "slow down."
    /// </summary>
    private static TimeSpan? TryGetRateLimitRetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter is { } retryAfter)
        {
            if (retryAfter.Delta is { } delta)
            {
                return delta;
            }

            if (retryAfter.Date is { } date)
            {
                var span = date - DateTimeOffset.UtcNow;
                if (span > TimeSpan.Zero)
                {
                    return span;
                }
            }
        }

        foreach (var header in response.Headers)
        {
            if (header.Key.Contains("RateLimit-Remaining", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(header.Value.FirstOrDefault(), out int remaining)
                && remaining <= 0)
            {
                return DefaultRateLimitBackoff;
            }
        }

        return null;
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
