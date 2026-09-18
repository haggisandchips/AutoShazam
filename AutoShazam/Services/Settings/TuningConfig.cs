using System.Globalization;
using System.IO;

namespace AutoShazam.Services.Settings;

/// <summary>
/// Advanced polling tuning, read once at startup from a flat "tuning.config" key=value file in the
/// app data folder - deliberately separate from <see cref="AppSettings"/>/settings.db, since this
/// isn't meant to be discoverable or changeable from the UI. It exists purely as a manual escape
/// hatch (e.g. for retuning how hard Auto Shazam hits Shazam's endpoint) that doesn't require a new
/// release. A missing file, or a missing/unparsable individual key, just falls back to the default.
/// </summary>
internal sealed record TuningConfig
{
    private const string FileName = "tuning.config";
    private static readonly TimeSpan DefaultMinQueryInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DefaultKnownSongQueryInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DefaultRateLimitFallbackBackoff = TimeSpan.FromSeconds(30);

    /// <summary>Floor for Auto Shazam's poll interval while it doesn't know what's currently
    /// playing.</summary>
    public TimeSpan MinQueryInterval { get; private init; } = DefaultMinQueryInterval;

    /// <summary>Poll interval once a check has confirmed what's playing - an absolute value, not a
    /// multiple of <see cref="MinQueryInterval"/>, so the two can be tuned independently.</summary>
    public TimeSpan KnownSongQueryInterval { get; private init; } = DefaultKnownSongQueryInterval;

    /// <summary>Backoff used when Shazam returns a 429 (or an equivalent rate-limit signal) without
    /// telling us how long to wait - see <see cref="Shazam.ShazamClient"/>'s header inspection, which
    /// honors an actual Retry-After header instead of this whenever Shazam sends one.</summary>
    public TimeSpan RateLimitFallbackBackoff { get; private init; } = DefaultRateLimitFallbackBackoff;

    public static TuningConfig Load(string appDataRoot)
    {
        var config = new TuningConfig();

        try
        {
            var path = Path.Combine(appDataRoot, FileName);
            if (File.Exists(path))
            {
                foreach (var (key, value) in ParseLines(File.ReadAllLines(path)))
                {
                    if (!TryParseSeconds(value, out var interval))
                    {
                        continue;
                    }

                    config = key.ToUpperInvariant() switch
                    {
                        "MINQUERYINTERVALSECONDS" => config with { MinQueryInterval = interval },
                        "KNOWNSONGQUERYINTERVALSECONDS" => config with { KnownSongQueryInterval = interval },
                        "RATELIMITFALLBACKBACKOFFSECONDS" => config with { RateLimitFallbackBackoff = interval },
                        _ => config,
                    };
                }
            }
        }
        catch
        {
            // Best-effort only - a missing/corrupt/unreadable file just means the defaults stand.
        }

        return config;
    }

    private static bool TryParseSeconds(string value, out TimeSpan interval)
    {
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
        {
            interval = TimeSpan.FromSeconds(seconds);
            return true;
        }

        interval = default;
        return false;
    }

    private static IEnumerable<(string Key, string Value)> ParseLines(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            int separator = trimmed.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            yield return (trimmed[..separator].Trim(), trimmed[(separator + 1)..].Trim());
        }
    }
}
