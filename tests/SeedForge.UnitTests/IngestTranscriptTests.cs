using Microsoft.Extensions.Logging.Abstractions;
using SeedForge.Domain;
using SeedForge.Features.Ingestion;
using SeedForge.Services.Apify;
using SeedForge.UnitTests.Fakes;

namespace SeedForge.UnitTests
{
    /// <summary>Proves the slice persists immutable Video + Transcript with the right status, distinguishes NoTranscript from Failed, and is idempotent on the YouTube id.</summary>
    public class IngestTranscriptTests : IDisposable
    {
        private readonly SliceTestHarness _h = new();
        private const string VideoId = "abc12345678";
        private const string Url = "https://www.youtube.com/watch?v=abc12345678";

        private IngestTranscriptHandler Build(FakeApifyIngestionService apify, Data.ApplicationDbContext db) =>
            new(db, apify, NullLogger<IngestTranscriptHandler>.Instance);

        [Fact]
        public async Task Success_persists_video_done_and_transcript_with_raw_item()
        {
            var apify = FakeApifyIngestionService.WithTranscript(VideoId, "the transcript text", cost: 1.5);

            using var db = _h.NewDb();
            var result = await Build(apify, db).HandleAsync(new(Url, "corr-1"), default);

            Assert.Equal(VideoJobStatus.Done, result.Status);
            Assert.NotNull(result.TranscriptId);

            using var read = _h.NewDb();
            var video = read.Videos.Single(v => v.Id == result.VideoId);
            Assert.Equal(VideoId, video.YouTubeVideoId);
            Assert.Equal(VideoJobStatus.Done, video.Status);
            Assert.Equal(1.5, video.ApifyCostUnits);

            var transcript = read.Transcripts.Single(t => t.Id == result.TranscriptId);
            Assert.Equal(video.Id, transcript.VideoId);
            Assert.Equal("the transcript text", transcript.PlainText);
            Assert.False(string.IsNullOrWhiteSpace(transcript.RawDatasetItemJson));
            Assert.Equal(1.5, transcript.ApifyCostUnits);
        }

        [Fact]
        public async Task Success_stamps_apify_metadata_onto_the_video()
        {
            var metadata = new Domain.VideoMetadata(
                DurationSeconds: 933, ViewCount: 1234567, LikeCount: 8901, CommentCount: 42,
                PublishedAtUtc: new DateTime(2026, 6, 28, 10, 0, 0, DateTimeKind.Utc),
                Description: "desc", ThumbnailUrl: "https://img/hq.jpg", YouTubeChannelId: "UC_chan",
                Source: Domain.MetadataSource.Apify);
            var apify = FakeApifyIngestionService.Returns(
                new IngestedVideo(true, "text", "Title", "Channel", """{"x":1}""", 1.0, VideoId, metadata));

            using var db = _h.NewDb();
            var result = await Build(apify, db).HandleAsync(new(Url, "corr-1"), default);

            using var read = _h.NewDb();
            var video = read.Videos.Single(v => v.Id == result.VideoId);
            Assert.Equal(933, video.DurationSeconds);
            Assert.Equal(1234567L, video.ViewCount);
            Assert.Equal(8901L, video.LikeCount);
            Assert.Equal(42L, video.CommentCount);
            Assert.Equal("UC_chan", video.YouTubeChannelId);
            Assert.Equal(Domain.MetadataSource.Apify, video.MetadataSource);
            Assert.NotNull(video.MetadataFetchedAtUtc);
        }

        [Fact]
        public async Task Ingest_merges_fresh_apify_with_pre_existing_discovery_metadata()
        {
            // Simulate discovery: a Video row already carrying YouTube metadata, not yet transcribed.
            using (var seed = _h.NewDb())
            {
                seed.Videos.Add(new Video
                {
                    YouTubeVideoId = VideoId,
                    Url = Url,
                    Status = VideoJobStatus.Pending,
                    CreatedAtUtc = DateTime.UtcNow,
                    ViewCount = 200,            // fresh YouTube stat
                    CommentCount = 9,           // YouTube-only field
                    YouTubeChannelId = "UC_yt",
                    MetadataSource = MetadataSource.YouTube,
                    MetadataFetchedAtUtc = DateTime.UtcNow,
                });
                seed.SaveChanges();
            }

            // Ingest brings the free Apify parse: duration + likes (gaps YouTube didn't have) and a staler view count.
            var apifyMeta = new VideoMetadata(
                DurationSeconds: 933, ViewCount: 100, LikeCount: 5, CommentCount: null,
                PublishedAtUtc: null, Description: "apify", ThumbnailUrl: "apify.jpg", YouTubeChannelId: null,
                Source: MetadataSource.Apify);
            var apify = FakeApifyIngestionService.Returns(
                new IngestedVideo(true, "text", "Title", "Channel", """{"x":1}""", 1.0, VideoId, apifyMeta));

            using var db = _h.NewDb();
            var result = await Build(apify, db).HandleAsync(new(Url, "corr-1"), default);

            using var read = _h.NewDb();
            var video = read.Videos.Single(v => v.Id == result.VideoId);
            Assert.Equal(MetadataSource.Merged, video.MetadataSource);
            Assert.Equal(200L, video.ViewCount);   // YouTube (fresher) wins the stat
            Assert.Equal(9L, video.CommentCount);  // preserved from discovery
            Assert.Equal(5L, video.LikeCount);     // Apify filled the gap
            Assert.Equal(933, video.DurationSeconds); // Apify-only
            Assert.Equal("UC_yt", video.YouTubeChannelId); // discovery value kept (Apify had none)
        }

        [Fact]
        public async Task No_metadata_leaves_source_None()
        {
            // A transcript with no parseable metadata (Metadata == null) must not flip MetadataSource off None.
            var apify = FakeApifyIngestionService.WithTranscript(VideoId, "text");

            using var db = _h.NewDb();
            var result = await Build(apify, db).HandleAsync(new(Url, "corr-1"), default);

            using var read = _h.NewDb();
            var video = read.Videos.Single(v => v.Id == result.VideoId);
            Assert.Equal(Domain.MetadataSource.None, video.MetadataSource);
            Assert.Null(video.MetadataFetchedAtUtc);
            Assert.Null(video.DurationSeconds);
        }

        [Fact]
        public async Task No_captions_records_NoTranscript_and_writes_no_transcript_row()
        {
            var apify = FakeApifyIngestionService.NoTranscript(VideoId);

            using var db = _h.NewDb();
            var result = await Build(apify, db).HandleAsync(new(Url, "corr-1"), default);

            Assert.Equal(VideoJobStatus.NoTranscript, result.Status);
            Assert.Null(result.TranscriptId);

            using var read = _h.NewDb();
            Assert.Equal(VideoJobStatus.NoTranscript, read.Videos.Single(v => v.Id == result.VideoId).Status);
            Assert.Empty(read.Transcripts.Where(t => t.VideoId == result.VideoId));
        }

        [Fact]
        public async Task Apify_error_records_Failed_with_message_and_no_transcript()
        {
            var apify = FakeApifyIngestionService.Throws("apify 500");

            using var db = _h.NewDb();
            var result = await Build(apify, db).HandleAsync(new(Url, "corr-1"), default);

            Assert.Equal(VideoJobStatus.Failed, result.Status);
            Assert.Null(result.TranscriptId);

            using var read = _h.NewDb();
            var video = read.Videos.Single(v => v.Id == result.VideoId);
            Assert.Equal(VideoJobStatus.Failed, video.Status);
            Assert.Contains("apify 500", video.ErrorMessage);
            Assert.Empty(read.Transcripts.Where(t => t.VideoId == result.VideoId));
        }

        [Fact]
        public async Task Re_ingesting_same_id_returns_existing_transcript_without_second_fetch()
        {
            var apify = FakeApifyIngestionService.WithTranscript(VideoId, "once");

            using var db1 = _h.NewDb();
            var first = await Build(apify, db1).HandleAsync(new(Url, "corr-1"), default);

            using var db2 = _h.NewDb();
            var second = await Build(apify, db2).HandleAsync(new(Url, "corr-2"), default);

            Assert.Equal(first.VideoId, second.VideoId);
            Assert.Equal(first.TranscriptId, second.TranscriptId);
            Assert.Equal(1, apify.CallCount); // no second paid fetch

            using var read = _h.NewDb();
            Assert.Single(read.Videos.Where(v => v.YouTubeVideoId == VideoId));
            Assert.Single(read.Transcripts.Where(t => t.VideoId == first.VideoId));
        }

        [Fact]
        public async Task Malformed_url_is_rejected_before_any_fetch()
        {
            var apify = FakeApifyIngestionService.WithTranscript(VideoId, "x");

            using var db = _h.NewDb();
            await Assert.ThrowsAsync<ArgumentException>(() =>
                Build(apify, db).HandleAsync(new("not a youtube url", "corr-1"), default));

            Assert.Equal(0, apify.CallCount);
        }

        public void Dispose() => _h.Dispose();
    }
}
