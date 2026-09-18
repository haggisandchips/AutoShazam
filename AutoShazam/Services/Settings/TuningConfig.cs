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
internal sealed class TuningConfig
{
    private const string FileName = "tuning.config";
    private static readonly TimeSpan DefaultMinQueryInterval = TimeSpan.FromSeconds(10);

    public TimeSpan MinQueryInterval { get; }

    private TuningConfig(TimeSpan minQueryInterval)
    {
        MinQueryInterval = minQueryInterval;
    }

    public static TuningConfig Load(string appDataRoot)
    {
        var minQueryInterval = DefaultMinQueryInterval;

        try
        {
            var path = Path.Combine(appDataRoot, FileName);
            if (File.Exists(path))
            {
                foreach (var (key, value) in ParseLines(File.ReadAllLines(path)))
                {
                    if (string.Equals(key, "MinQueryIntervalSeconds", StringComparison.OrdinalIgnoreCase)
                        && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                        && seconds > 0)
                    {
                        minQueryInterval = TimeSpan.FromSeconds(seconds);
                    }
                }
            }
        }
        catch
        {
            // Best-effort only - a missing/corrupt/unreadable file just means the default stands.
        }

        return new TuningConfig(minQueryInterval);
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
