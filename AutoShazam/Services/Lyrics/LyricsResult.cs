using AutoShazam.Models;

namespace AutoShazam.Services.Lyrics;

/// <summary>The outcome of a lyrics lookup. <see cref="SyncedLines"/> is only ever populated by
/// <see cref="LrcLibProvider"/> - every other provider only returns <see cref="PlainText"/>.</summary>
internal sealed record LyricsResult(string? PlainText, IReadOnlyList<LyricLine>? SyncedLines);
