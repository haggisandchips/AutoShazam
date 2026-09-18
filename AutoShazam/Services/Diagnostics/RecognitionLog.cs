using System.IO;

namespace AutoShazam.Services.Diagnostics;

/// <summary>
/// A small best-effort rolling log of what each recognition attempt actually captured and what
/// Shazam returned for it - e.g. fingerprint peak count and whether a track came back. Exists so
/// that "it didn't recognize this song" can be diagnosed after the fact (bad signature vs. genuine
/// miss) without needing to reproduce the audio live.
/// </summary>
internal sealed class RecognitionLog
{
    private const long MaxSizeBytes = 2 * 1024 * 1024;

    /// <summary>How many rotated-out logs (recognition.log.1 .. .5) are kept alongside the current
    /// one - once full, the oldest archive is dropped rather than the whole history at once.</summary>
    private const int MaxArchives = 5;

    private readonly string _filePath;
    private readonly object _sync = new();

    public RecognitionLog(string appDataRoot)
    {
        _filePath = Path.Combine(appDataRoot, "recognition.log");
    }

    public void Write(string message)
    {
        try
        {
            lock (_sync)
            {
                if (File.Exists(_filePath) && new FileInfo(_filePath).Length > MaxSizeBytes)
                {
                    RotateLocked();
                }

                File.AppendAllText(_filePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Best-effort diagnostics only - never let logging itself break recognition.
        }
    }

    /// <summary>Shifts recognition.log -> .1 -> .2 .. up to <see cref="MaxArchives"/>, dropping
    /// whichever archive is oldest, instead of just deleting the file outright once it gets big -
    /// keeps a little history around for diagnosing a problem that's only noticed after the log
    /// covering it would otherwise already have been thrown away. Must be called with
    /// <see cref="_sync"/> already held.</summary>
    private void RotateLocked()
    {
        var oldest = ArchivePath(MaxArchives);
        if (File.Exists(oldest))
        {
            File.Delete(oldest);
        }

        for (int i = MaxArchives - 1; i >= 1; i--)
        {
            var source = ArchivePath(i);
            if (File.Exists(source))
            {
                File.Move(source, ArchivePath(i + 1));
            }
        }

        File.Move(_filePath, ArchivePath(1));
    }

    private string ArchivePath(int index) => $"{_filePath}.{index}";
}
