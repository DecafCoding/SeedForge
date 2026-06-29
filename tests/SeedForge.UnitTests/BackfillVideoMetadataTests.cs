using Microsoft.Extensions.Logging.Abstractions;
using SeedForge.Domain;
using SeedForge.Features.Maintenance;
using SeedForge.Services.YouTube;
using SeedForge.UnitTests.Fakes;

namespace SeedForge.UnitTests
{
    /// <summary>
    /// The backfill re-parses each pre-Phase-8 video's stored Apify item to populate metadata — with no re-fetch —
    /// and is idempotent: a second run changes nothing, and it never overwrites YouTube-sourced rows.
    /// </summary>
    public class BackfillVideoMetadataTests : IDisposable
    {
        private readonly SliceTestHarness _h = new();

        private BackfillVideoMetadataHandler Build(Data.ApplicationDbContext db) =>
            new(db, NullLogger<BackfillVideoMetadataHandler>.Instance);

        private int SeedVideoWithTranscript(string youTubeId, string rawJson, MetadataSource source = MetadataSource.None)
        {
            using var db = _h.NewDb();
            var video = new Video
            {
                YouTubeVideoId = youTubeId,
                Url = YouTubeUrl.WatchUrl(youTubeId),
                Status = VideoJobStatus.Done,
                MetadataSource = source,
                CreatedAtUtc = DateTime.UtcNow,
            };
            db.Videos.Add(video);
            db.SaveChanges();

            db.Transcripts.Add(new Transcript
            {
                VideoId = video.Id,
                PlainText = "text",
                RawDatasetItemJson = rawJson,
                CreatedAtUtc = DateTime.UtcNow,
            });
            db.SaveChanges();
            return video.Id;
        }

        [Fact]
        public async Task Backfills_metadata_from_stored_raw_item_then_is_idempotent()
        {
            var id = SeedVideoWithTranscript("vid00000001", """{ "duration": 933, "viewCount": 1000, "channelId": "UC_x" }""");

            using (var db = _h.NewDb())
            {
                var first = await Build(db).HandleAsync();
                Assert.Equal(1, first.Scanned);
                Assert.Equal(1, first.Updated);
            }

            using (var read = _h.NewDb())
            {
                var v = read.Videos.Single(x => x.Id == id);
                Assert.Equal(933, v.DurationSeconds);
                Assert.Equal(1000L, v.ViewCount);
                Assert.Equal("UC_x", v.YouTubeChannelId);
                Assert.Equal(MetadataSource.Apify, v.MetadataSource);
                Assert.NotNull(v.MetadataFetchedAtUtc);
            }

            // Re-run: the row is no longer None ⇒ not a candidate ⇒ no work.
            using (var db = _h.NewDb())
            {
                var second = await Build(db).HandleAsync();
                Assert.Equal(0, second.Scanned);
                Assert.Equal(0, second.Updated);
            }
        }

        [Fact]
        public async Task Does_not_touch_videos_already_sourced_from_youtube()
        {
            var id = SeedVideoWithTranscript(
                "vid00000002", """{ "duration": 100 }""", source: MetadataSource.YouTube);

            using var db = _h.NewDb();
            var result = await Build(db).HandleAsync();

            Assert.Equal(0, result.Scanned); // YouTube-sourced row is excluded from candidates

            using var read = _h.NewDb();
            var v = read.Videos.Single(x => x.Id == id);
            Assert.Equal(MetadataSource.YouTube, v.MetadataSource);
            Assert.Null(v.DurationSeconds); // untouched
        }

        [Fact]
        public async Task Video_whose_raw_item_has_no_metadata_stays_None()
        {
            SeedVideoWithTranscript("vid00000003", """{ "title": "nothing useful" }""");

            using var db = _h.NewDb();
            var result = await Build(db).HandleAsync();

            Assert.Equal(1, result.Scanned);
            Assert.Equal(0, result.Updated); // parse yielded nothing usable

            using var read = _h.NewDb();
            Assert.Equal(MetadataSource.None, read.Videos.Single(v => v.YouTubeVideoId == "vid00000003").MetadataSource);
        }

        public void Dispose() => _h.Dispose();
    }
}
