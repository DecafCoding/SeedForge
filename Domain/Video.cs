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

        // Queue mechanics (Phase 5): the Video row is the Processing-worker job.
        /// <summary>Number of failed processing attempts; drives backoff and the terminal-Failed cap.</summary>
        public int AttemptCount { get; set; }
        /// <summary>Earliest UTC the job may be claimed again after a backoff; null ⇒ immediately eligible.</summary>
        public DateTime? NextAttemptUtc { get; set; }
        /// <summary>Higher claims first; "process now" raises this so the row jumps the line.</summary>
        public int Priority { get; set; }
    }
}
