using System.Text.Json.Serialization;

namespace AutoShazam.Services.Shazam;

internal sealed class ShazamRecognizeResponse
{
    [JsonPropertyName("matches")]
    public List<ShazamMatch>? Matches { get; set; }

    [JsonPropertyName("track")]
    public ShazamTrack? Track { get; set; }
}

internal sealed class ShazamMatch
{
    /// <summary>Seconds into the reference track where the fingerprinted clip matched - used to
    /// estimate the track's current playback position for lyrics sync.</summary>
    [JsonPropertyName("offset")]
    public double? Offset { get; set; }
}

internal sealed class ShazamTrack
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("subtitle")]
    public string? Subtitle { get; set; }

    [JsonPropertyName("images")]
    public ShazamImages? Images { get; set; }
}

internal sealed class ShazamImages
{
    [JsonPropertyName("background")]
    public string? Background { get; set; }

    [JsonPropertyName("coverart")]
    public string? CoverArt { get; set; }

    [JsonPropertyName("coverarthq")]
    public string? CoverArtHq { get; set; }
}
