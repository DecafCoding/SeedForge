using SeedForge.Domain;

namespace SeedForge.UnitTests
{
    /// <summary>
    /// The merge rule: YouTube wins the volatile stats (views/likes/comments/publish date) and ties on identity fields;
    /// Apify fills any gaps; duration comes from whichever has it; provenance becomes Merged only when both contribute.
    /// </summary>
    public class VideoMetadataMergeTests
    {
        private static VideoMetadata Apify(
            int? duration = null, long? views = null, long? likes = null, long? comments = null,
            DateTime? published = null, string? desc = null, string? thumb = null, string? channel = null) =>
            new(duration, views, likes, comments, published, desc, thumb, channel, MetadataSource.Apify);

        private static VideoMetadata YouTube(
            int? duration = null, long? views = null, long? likes = null, long? comments = null,
            DateTime? published = null, string? desc = null, string? thumb = null, string? channel = null) =>
            new(duration, views, likes, comments, published, desc, thumb, channel, MetadataSource.YouTube);

        [Fact]
        public void Youtube_wins_stats_and_apify_fills_gaps()
        {
            var apify = Apify(duration: 933, views: 100, likes: 5, desc: "apify desc", thumb: "apify.jpg");
            var youtube = YouTube(views: 200, comments: 9, channel: "UC_yt"); // fresher views, has comments + channel

            var merged = VideoMetadataMerge.Combine(apify, youtube)!;

            Assert.Equal(200L, merged.ViewCount);          // YouTube wins the count
            Assert.Equal(5L, merged.LikeCount);            // only Apify had likes ⇒ filled
            Assert.Equal(9L, merged.CommentCount);         // only YouTube had comments
            Assert.Equal(933, merged.DurationSeconds);     // only Apify had duration
            Assert.Equal("apify desc", merged.Description); // YouTube blank ⇒ Apify fills
            Assert.Equal("UC_yt", merged.YouTubeChannelId);
            Assert.Equal(MetadataSource.Merged, merged.Source);
        }

        [Fact]
        public void Youtube_wins_ties_on_identity_fields()
        {
            var apify = Apify(thumb: "apify.jpg", channel: "UC_apify");
            var youtube = YouTube(thumb: "youtube.jpg", channel: "UC_youtube");

            var merged = VideoMetadataMerge.Combine(apify, youtube)!;

            Assert.Equal("youtube.jpg", merged.ThumbnailUrl);
            Assert.Equal("UC_youtube", merged.YouTubeChannelId);
        }

        [Fact]
        public void Single_source_is_returned_with_its_own_provenance()
        {
            var apify = Apify(views: 1);
            Assert.Same(apify, VideoMetadataMerge.Combine(apify, null));

            var youtube = YouTube(views: 1);
            Assert.Same(youtube, VideoMetadataMerge.Combine(null, youtube));

            Assert.Null(VideoMetadataMerge.Combine(null, null));
        }
    }
}
