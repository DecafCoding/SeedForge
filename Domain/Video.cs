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

        // Video metadata (Phase 8): best-effort, additive. All nullable — null means "unknown", never zero.
        // Captured from the Apify dataset item (free) and/or the YouTube videos.list call (fresher).
        /// <summary>Length in seconds; from Apify (seconds / clock) or YouTube (ISO-8601). Null when unknown.</summary>
        public int? DurationSeconds { get; set; }
        /// <summary>Lifetime view count; <see cref="long"/> because counts overflow <see cref="int"/>. Null when unknown.</summary>
        public long? ViewCount { get; set; }
        /// <summary>Like count; null when hidden by the uploader (distinct from zero).</summary>
        public long? LikeCount { get; set; }
        /// <summary>Comment count; usually only supplied by YouTube. Null when unknown.</summary>
        public long? CommentCount { get; set; }
        /// <summary>Original publish/upload time (UTC). Null when unknown.</summary>
        public DateTime? PublishedAtUtc { get; set; }
        /// <summary>Video description; can be long. Truncate on display, not on storage.</summary>
        public string? Description { get; set; }
        /// <summary>Highest-resolution thumbnail URL available.</summary>
        public string? ThumbnailUrl { get; set; }
        /// <summary>The video's YouTube channel id, recorded even for a single-video ingest with no <see cref="Channel"/> row.</summary>
        public string? YouTubeChannelId { get; set; }
        /// <summary>Provenance of the captured metadata; <see cref="MetadataSource.None"/> until anything is captured.</summary>
        public MetadataSource MetadataSource { get; set; } = MetadataSource.None;
        /// <summary>When the metadata was last captured/refreshed (stats drift over time). Null when never captured.</summary>
        public DateTime? MetadataFetchedAtUtc { get; set; }
    }
}
