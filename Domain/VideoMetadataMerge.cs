namespace SeedForge.Domain
{
    /// <summary>
    /// The explicit precedence rule for resolving <see cref="VideoMetadata"/> when both an Apify parse and a YouTube
    /// <c>videos.list</c> fetch carry values. YouTube is treated as canonical and fresher for the volatile stats
    /// (view/like/comment counts, publish date) and wins ties for identity fields; Apify fills any gaps. Duration is
    /// taken from whichever source has it (they agree). The result's <see cref="VideoMetadata.Source"/> is
    /// <see cref="MetadataSource.Merged"/> when both contribute, otherwise the single contributor's source.
    /// </summary>
    public static class VideoMetadataMerge
    {
        /// <summary>Combines the two best-effort sources. Either may be null; returns null only when both are null.</summary>
        public static VideoMetadata? Combine(VideoMetadata? apify, VideoMetadata? youtube)
        {
            if (apify is null) return youtube;
            if (youtube is null) return apify;

            return new VideoMetadata(
                DurationSeconds: youtube.DurationSeconds ?? apify.DurationSeconds,
                ViewCount: youtube.ViewCount ?? apify.ViewCount,
                LikeCount: youtube.LikeCount ?? apify.LikeCount,
                CommentCount: youtube.CommentCount ?? apify.CommentCount,
                PublishedAtUtc: youtube.PublishedAtUtc ?? apify.PublishedAtUtc,
                Description: Coalesce(youtube.Description, apify.Description),
                ThumbnailUrl: Coalesce(youtube.ThumbnailUrl, apify.ThumbnailUrl),
                YouTubeChannelId: Coalesce(youtube.YouTubeChannelId, apify.YouTubeChannelId),
                Source: MetadataSource.Merged);
        }

        /// <summary>First non-blank string; null when both are blank.</summary>
        private static string? Coalesce(string? primary, string? fallback) =>
            !string.IsNullOrWhiteSpace(primary) ? primary
            : !string.IsNullOrWhiteSpace(fallback) ? fallback
            : null;
    }
}
