namespace SeedForge.Domain
{
    /// <summary>Immutable source that doubles as the Processing-worker job: a discovered video, deduped by YouTube id.</summary>
    public class Video
    {
        public int Id { get; set; }
        public string YouTubeVideoId { get; set; } = string.Empty;
        public int? ChannelId { get; set; }
        public string Url { get; set; } = string.Empty;
        public string? Title { get; set; }
        public VideoJobStatus Status { get; set; }
        public double? ApifyCostUnits { get; set; }
        public string? ErrorMessage { get; set; }
        public DateTime CreatedAtUtc { get; set; }
    }
}
