using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SeedForge.Domain;
using SeedForge.Features.Discovery;
using SeedForge.Services.Queues;
using SeedForge.Services.YouTube;
using SeedForge.UnitTests.Fakes;
using SeedForge.Workers;

namespace SeedForge.UnitTests
{
    /// <summary>PollChannels enqueues only genuinely new uploads, leaves known ones, stamps LastPolledUtc, and does no AI/transcript work.</summary>
    public class PollChannelsTests : IDisposable
    {
        private readonly SliceTestHarness _h = new();
        private readonly WorkerOptions _opts = new() { MaxAttempts = 5, BackoffBaseSeconds = 30 };
        private const string UploadsId = "UU_x5XG1OV2P6uZZ5FSM9Ttw";

        private int SeedChannel(string channelId = "UC_x5XG1OV2P6uZZ5FSM9Ttw", string uploads = UploadsId)
        {
            using var db = _h.NewDb();
            var channel = new Channel
            {
                YouTubeChannelId = channelId,
                Title = "Test Channel",
                UploadsPlaylistId = uploads,
                AddedAtUtc = DateTime.UtcNow,
            };
            db.Channels.Add(channel);
            db.SaveChanges();
            return channel.Id;
        }

        private void SeedVideo(string youTubeVideoId)
        {
            using var db = _h.NewDb();
            db.Videos.Add(new Video
            {
                YouTubeVideoId = youTubeVideoId,
                Url = YouTubeUrl.WatchUrl(youTubeVideoId),
                Status = VideoJobStatus.Done,
                CreatedAtUtc = DateTime.UtcNow,
            });
            db.SaveChanges();
        }

        // The handler + queue share one DbContext (mirrors the per-iteration scope in the app).
        private PollChannelsHandler NewHandler(
            FakeYouTubeDataClient yt, SeedForge.Data.ApplicationDbContext db, bool fetchVideoMetadata = false)
        {
            var queue = new VideoQueue(db, Options.Create(_opts), new WorkerControl(), NullLogger<VideoQueue>.Instance);
            var ytOptions = Options.Create(new YouTubeOptions { FetchVideoMetadata = fetchVideoMetadata });
            return new PollChannelsHandler(db, yt, queue, ytOptions, NullLogger<PollChannelsHandler>.Instance);
        }

        [Fact]
        public async Task Poll_enqueues_only_the_new_ids_and_stamps_LastPolledUtc()
        {
            var channelId = SeedChannel();
            SeedVideo("known111111"); // already known ⇒ must not be re-enqueued

            var yt = new FakeYouTubeDataClient()
                .HasRecent(UploadsId, "new00000001", "known111111", "new00000002"); // 3 recent, 1 known

            using var db = _h.NewDb();
            var result = await NewHandler(yt, db).HandleAsync(new PollChannelsRequest(channelId), CancellationToken.None);

            Assert.Single(result.Channels);
            Assert.Equal(2, result.Channels[0].NewVideoCount);

            using var read = _h.NewDb();
            // The two new ids are now Pending Video rows; the known one is untouched (still Done) and not duplicated.
            Assert.Equal(VideoJobStatus.Pending, read.Videos.Single(v => v.YouTubeVideoId == "new00000001").Status);
            Assert.Equal(VideoJobStatus.Pending, read.Videos.Single(v => v.YouTubeVideoId == "new00000002").Status);
            Assert.Equal(VideoJobStatus.Done, read.Videos.Single(v => v.YouTubeVideoId == "known111111").Status);
            Assert.Equal(1, read.Videos.Count(v => v.YouTubeVideoId == "known111111"));

            // LastPolledUtc stamped; no transcript/concept work done.
            Assert.NotNull(read.Channels.Single(c => c.Id == channelId).LastPolledUtc);
            Assert.Empty(read.Transcripts);
            Assert.Empty(read.Concepts);
        }

        [Fact]
        public async Task Poll_persists_the_upload_title_on_the_new_video()
        {
            var channelId = SeedChannel();
            var yt = new FakeYouTubeDataClient()
                .HasRecentUploads(UploadsId, new RecentUpload("new00000001", "How to Terraform Mars"));

            using var db = _h.NewDb();
            await NewHandler(yt, db).HandleAsync(new PollChannelsRequest(channelId), CancellationToken.None);

            using var read = _h.NewDb();
            Assert.Equal("How to Terraform Mars", read.Videos.Single(v => v.YouTubeVideoId == "new00000001").Title);
        }

        [Fact]
        public async Task Poll_with_no_new_uploads_enqueues_nothing_but_still_stamps()
        {
            var channelId = SeedChannel();
            SeedVideo("known111111");
            var yt = new FakeYouTubeDataClient().HasRecent(UploadsId, "known111111");

            using var db = _h.NewDb();
            var result = await NewHandler(yt, db).HandleAsync(new PollChannelsRequest(channelId), CancellationToken.None);

            Assert.Equal(0, result.Channels[0].NewVideoCount);
            using var read = _h.NewDb();
            Assert.Equal(1, read.Videos.Count()); // no new rows
            Assert.NotNull(read.Channels.Single(c => c.Id == channelId).LastPolledUtc);
        }

        [Fact]
        public async Task Poll_with_enrichment_on_stamps_youtube_metadata_on_new_videos()
        {
            var channelId = SeedChannel();
            var yt = new FakeYouTubeDataClient()
                .HasRecent(UploadsId, "new00000001", "new00000002")
                .HasMetadata("new00000001", new VideoMetadata(
                    DurationSeconds: 600, ViewCount: 12345, LikeCount: 678, CommentCount: 9,
                    PublishedAtUtc: new DateTime(2026, 6, 28, 0, 0, 0, DateTimeKind.Utc),
                    Description: "d", ThumbnailUrl: "https://img/x.jpg", YouTubeChannelId: "UC_chan",
                    Source: MetadataSource.YouTube));

            using var db = _h.NewDb();
            await NewHandler(yt, db, fetchVideoMetadata: true)
                .HandleAsync(new PollChannelsRequest(channelId), CancellationToken.None);

            Assert.Equal(1, yt.MetadataCalls); // one batched videos.list call

            using var read = _h.NewDb();
            var enriched = read.Videos.Single(v => v.YouTubeVideoId == "new00000001");
            Assert.Equal(600, enriched.DurationSeconds);
            Assert.Equal(12345L, enriched.ViewCount);
            Assert.Equal(MetadataSource.YouTube, enriched.MetadataSource);
            Assert.NotNull(enriched.MetadataFetchedAtUtc);

            // The id with no metadata returned stays metadata-less (None) — absence is not an error.
            var bare = read.Videos.Single(v => v.YouTubeVideoId == "new00000002");
            Assert.Equal(MetadataSource.None, bare.MetadataSource);
        }

        [Fact]
        public async Task Poll_skips_videos_under_the_minimum_duration_without_enqueuing()
        {
            var channelId = SeedChannel();
            var yt = new FakeYouTubeDataClient()
                .HasRecent(UploadsId, "shortvid001", "longvid0001")
                .HasMetadata("shortvid001", Meta(durationSeconds: 120))  // under 3m ⇒ skipped
                .HasMetadata("longvid0001", Meta(durationSeconds: 600)); // over 3m ⇒ enqueued

            using var db = _h.NewDb();
            var result = await NewHandler(yt, db, fetchVideoMetadata: true)
                .HandleAsync(new PollChannelsRequest(channelId), CancellationToken.None);

            // Only the long video counts as a new (enqueued) upload.
            Assert.Equal(1, result.Channels[0].NewVideoCount);

            using var read = _h.NewDb();
            var shortVideo = read.Videos.Single(v => v.YouTubeVideoId == "shortvid001");
            Assert.Equal(VideoJobStatus.SkippedShort, shortVideo.Status); // recorded, never queued
            Assert.Equal(120, shortVideo.DurationSeconds);                // metadata kept for inspection

            Assert.Equal(VideoJobStatus.Pending, read.Videos.Single(v => v.YouTubeVideoId == "longvid0001").Status);

            // The skipped row is a dedup marker; no transcript work happened.
            Assert.Empty(read.Transcripts);
        }

        [Fact]
        public async Task Poll_skipped_short_video_is_not_re_enqueued_on_a_later_poll()
        {
            var channelId = SeedChannel();
            var yt = new FakeYouTubeDataClient()
                .HasRecent(UploadsId, "shortvid001")
                .HasMetadata("shortvid001", Meta(durationSeconds: 90));

            using (var db = _h.NewDb())
            {
                await NewHandler(yt, db, fetchVideoMetadata: true)
                    .HandleAsync(new PollChannelsRequest(channelId), CancellationToken.None);
            }

            // Second poll sees the same id — it is already known, so nothing new is added and it stays SkippedShort.
            using var db2 = _h.NewDb();
            var result = await NewHandler(yt, db2, fetchVideoMetadata: true)
                .HandleAsync(new PollChannelsRequest(channelId), CancellationToken.None);

            Assert.Equal(0, result.Channels[0].NewVideoCount);
            using var read = _h.NewDb();
            Assert.Equal(1, read.Videos.Count(v => v.YouTubeVideoId == "shortvid001"));
            Assert.Equal(VideoJobStatus.SkippedShort, read.Videos.Single(v => v.YouTubeVideoId == "shortvid001").Status);
        }

        private static VideoMetadata Meta(int durationSeconds) => new(
            DurationSeconds: durationSeconds, ViewCount: null, LikeCount: null, CommentCount: null,
            PublishedAtUtc: null, Description: null, ThumbnailUrl: null, YouTubeChannelId: null,
            Source: MetadataSource.YouTube);

        [Fact]
        public async Task Poll_with_enrichment_off_makes_no_videos_list_call()
        {
            var channelId = SeedChannel();
            var yt = new FakeYouTubeDataClient().HasRecent(UploadsId, "new00000001");

            using var db = _h.NewDb();
            await NewHandler(yt, db, fetchVideoMetadata: false)
                .HandleAsync(new PollChannelsRequest(channelId), CancellationToken.None);

            Assert.Equal(0, yt.MetadataCalls); // flag off ⇒ zero extra quota
            using var read = _h.NewDb();
            Assert.Equal(MetadataSource.None, read.Videos.Single(v => v.YouTubeVideoId == "new00000001").MetadataSource);
        }

        public void Dispose() => _h.Dispose();
    }
}
