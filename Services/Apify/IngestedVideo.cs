using SeedForge.Domain;

namespace SeedForge.Services.Apify
{
    /// <summary>
    /// The outcome of fetching one video from Apify: whether a transcript was found, the joined text + metadata,
    /// the raw first dataset item (stored so a future schema drift can be re-parsed), the deterministic pay-per-result
    /// cost in USD (0 when the run returned no billable video), and the per-video <see cref="VideoMetadata"/> parsed
    /// (free) from that same raw item.
    /// </summary>
    public sealed record IngestedVideo(
        bool HadTranscript,
        string? Text,
        string? Title,
        string? ChannelName,
        string RawItemJson,
        double? CostUsd,
        string YouTubeVideoId,
        VideoMetadata? Metadata = null);
}
