namespace AutoShazam.Models;

/// <summary>One line of time-synced lyrics, as parsed from an LRC file.</summary>
public sealed record LyricLine(TimeSpan Timestamp, string Text);
