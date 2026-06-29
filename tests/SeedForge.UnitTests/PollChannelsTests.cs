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
        private PollChannelsHandler NewHandler(FakeYouTubeDataClient yt, SeedForge.Data.ApplicationDbContext db)
        {
            var queue = new VideoQueue(db, Options.Create(_opts), new WorkerControl(), NullLogger<VideoQueue>.Instance);
            return new PollChannelsHandler(db, yt, queue, NullLogger<PollChannelsHandler>.Instance);
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

        public void Dispose() => _h.Dispose();
    }
}
