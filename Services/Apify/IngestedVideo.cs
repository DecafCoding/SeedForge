namespace SeedForge.Services.Apify
{
    /// <summary>
    /// The outcome of fetching one video from Apify: whether a transcript was found, the joined text + metadata,
    /// the raw first dataset item (stored so a future schema drift can be re-parsed), and best-effort cost.
    /// </summary>
    public sealed record IngestedVideo(
        bool HadTranscript,
        string? Text,
        string? Title,
        string? ChannelName,
        string RawItemJson,
        double? CostUnits,
        string YouTubeVideoId);
}
