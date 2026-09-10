using System.Text.Json.Serialization;

namespace AutoShazam.Services.Shazam;

internal sealed class ShazamRecognizeResponse
{
    [JsonPropertyName("matches")]
    public List<object>? Matches { get; set; }

    [JsonPropertyName("track")]
    public ShazamTrack? Track { get; set; }
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
