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
                    File.Delete(_filePath);
                }

                File.AppendAllText(_filePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Best-effort diagnostics only - never let logging itself break recognition.
        }
    }
}
