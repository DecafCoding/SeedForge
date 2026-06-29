using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SeedForge.Domain;
using SeedForge.Features.Discovery;
using SeedForge.Services.Queues;
using SeedForge.UnitTests.Fakes;
using SeedForge.Workers;

namespace SeedForge.UnitTests
{
    /// <summary>The Discovery worker's extracted iteration polls the whole library; one channel's failure doesn't abort the rest.</summary>
    public class DiscoveryWorkerTests : IDisposable
    {
        private readonly SliceTestHarness _h = new();
        private readonly WorkerOptions _opts = new() { MaxAttempts = 5, BackoffBaseSeconds = 30 };

        private int SeedChannel(string channelId, string uploads)
        {
            using var db = _h.NewDb();
            var channel = new Channel
            {
                YouTubeChannelId = channelId,
                Title = $"Channel {channelId}",
                UploadsPlaylistId = uploads,
                AddedAtUtc = DateTime.UtcNow,
            };
            db.Channels.Add(channel);
            db.SaveChanges();
            return channel.Id;
        }

        private DiscoveryIteration NewIteration(FakeYouTubeDataClient yt, SeedForge.Data.ApplicationDbContext db)
        {
            var queue = new VideoQueue(db, Options.Create(_opts), new WorkerControl(), NullLogger<VideoQueue>.Instance);
            var poll = new PollChannelsHandler(
                db, yt, queue, Options.Create(new SeedForge.Services.YouTube.YouTubeOptions()),
                NullLogger<PollChannelsHandler>.Instance);
            return new DiscoveryIteration(poll, NullLogger<DiscoveryIteration>.Instance);
        }

        [Fact]
        public async Task ProcessOnceAsync_enqueues_new_ids_across_all_channels()
        {
            SeedChannel("UCaaaaaaaaaaaaaaaaaaaaaa", "UUaaaaaaaaaaaaaaaaaaaaaa");
            SeedChannel("UCbbbbbbbbbbbbbbbbbbbbbb", "UUbbbbbbbbbbbbbbbbbbbbbb");
            var yt = new FakeYouTubeDataClient()
                .HasRecent("UUaaaaaaaaaaaaaaaaaaaaaa", "aaa00000001", "aaa00000002")
                .HasRecent("UUbbbbbbbbbbbbbbbbbbbbbb", "bbb00000001");

            using var db = _h.NewDb();
            var enqueued = await NewIteration(yt, db).ProcessOnceAsync(CancellationToken.None);

            Assert.Equal(3, enqueued);
            using var read = _h.NewDb();
            Assert.Equal(3, read.Videos.Count(v => v.Status == VideoJobStatus.Pending));
        }

        [Fact]
        public async Task ProcessOnceAsync_keeps_polling_when_one_channel_fails()
        {
            SeedChannel("UCaaaaaaaaaaaaaaaaaaaaaa", "UUaaaaaaaaaaaaaaaaaaaaaa"); // this one throws
            SeedChannel("UCbbbbbbbbbbbbbbbbbbbbbb", "UUbbbbbbbbbbbbbbbbbbbbbb");
            var yt = new FakeYouTubeDataClient()
                .ThrowsOnList("UUaaaaaaaaaaaaaaaaaaaaaa")
                .HasRecent("UUbbbbbbbbbbbbbbbbbbbbbb", "bbb00000001", "bbb00000002");

            using var db = _h.NewDb();
            var enqueued = await NewIteration(yt, db).ProcessOnceAsync(CancellationToken.None);

            // The failing channel contributes 0; the healthy channel still enqueues its 2 new uploads.
            Assert.Equal(2, enqueued);
            using var read = _h.NewDb();
            Assert.Equal(2, read.Videos.Count(v => v.Status == VideoJobStatus.Pending));
        }

        public void Dispose() => _h.Dispose();
    }
}
