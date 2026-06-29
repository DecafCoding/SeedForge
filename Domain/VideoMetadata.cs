namespace SeedForge.Domain
{
    /// <summary>
    /// A pure, source-agnostic bundle of the per-video metadata captured from Apify and/or YouTube. All fields are
    /// nullable and best-effort: null means "unknown", never zero. <see cref="Source"/> records who produced it.
    /// </summary>
    public sealed record VideoMetadata(
        int? DurationSeconds,
        long? ViewCount,
        long? LikeCount,
        long? CommentCount,
        DateTime? PublishedAtUtc,
        string? Description,
        string? ThumbnailUrl,
        string? YouTubeChannelId,
        MetadataSource Source)
    {
        /// <summary>
        /// Reconstructs the metadata currently stored on a <see cref="Video"/> row (preserving its
        /// <see cref="Video.MetadataSource"/>), so a fresh parse can be merged with what's already there. Returns null
        /// when the row has no captured metadata (<see cref="MetadataSource.None"/>).
        /// </summary>
        public static VideoMetadata? FromVideo(Video video) =>
            video.MetadataSource == MetadataSource.None
                ? null
                : new VideoMetadata(
                    video.DurationSeconds, video.ViewCount, video.LikeCount, video.CommentCount,
                    video.PublishedAtUtc, video.Description, video.ThumbnailUrl, video.YouTubeChannelId,
                    video.MetadataSource);

        /// <summary>True when at least one substantive field carries a value (provenance/source aside).</summary>
        public bool HasAnyValue =>
            DurationSeconds is not null || ViewCount is not null || LikeCount is not null
            || CommentCount is not null || PublishedAtUtc is not null
            || !string.IsNullOrWhiteSpace(Description) || !string.IsNullOrWhiteSpace(ThumbnailUrl)
            || !string.IsNullOrWhiteSpace(YouTubeChannelId);

        /// <summary>
        /// Stamps this (already-merged) metadata onto a <see cref="Video"/> row, recording <see cref="Source"/> as the
        /// provenance and <paramref name="fetchedAtUtc"/> as the freshness. Copies every field verbatim — merge the
        /// sources first (see the merge rule) when more than one source is in play.
        /// </summary>
        public void ApplyTo(Video video, DateTime fetchedAtUtc)
        {
            video.DurationSeconds = DurationSeconds;
            video.ViewCount = ViewCount;
            video.LikeCount = LikeCount;
            video.CommentCount = CommentCount;
            video.PublishedAtUtc = PublishedAtUtc;
            video.Description = Description;
            video.ThumbnailUrl = ThumbnailUrl;
            video.YouTubeChannelId = YouTubeChannelId;
            video.MetadataSource = Source;
            video.MetadataFetchedAtUtc = fetchedAtUtc;
        }
    }
}
