using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SeedForge.Data;
using SeedForge.Domain;
using SeedForge.Features.Browse;

namespace SeedForge.UnitTests
{
    /// <summary>
    /// Projection tests for <see cref="BrowseQueries.IdeaRowsAsync"/> over seeded SQLite-in-memory rows: latest-score
    /// selection, the left-join that keeps pasted-transcript ideas, unscored ideas, and newest-first ordering.
    /// </summary>
    public sealed class BrowseQueriesTests : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly DbContextOptions<ApplicationDbContext> _dbOptions;
        private static readonly DateTime Now = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        public BrowseQueriesTests()
        {
            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();
            _dbOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(_connection).Options;
            using var db = NewDb();
            db.Database.EnsureCreated();
        }

        private ApplicationDbContext NewDb() => new(_dbOptions);

        /// <summary>Seeds a Video → Transcript → Segment → Idea lineage and returns the idea id.</summary>
        private int SeedVideoIdea(string premise, DateTime createdAt, string? videoUrl = "https://youtu.be/abc", string? videoTitle = "A Video")
        {
            using var db = NewDb();
            int? videoId = null;
            if (videoUrl is not null)
            {
                var video = new Video { YouTubeVideoId = $"yt-{premise}", Url = videoUrl, Title = videoTitle, Status = VideoJobStatus.Done, CreatedAtUtc = createdAt };
                db.Videos.Add(video);
                db.SaveChanges();
                videoId = video.Id;
            }
            var transcript = new Transcript { VideoId = videoId, PlainText = "text", Title = "Transcript title", CreatedAtUtc = createdAt };
            db.Transcripts.Add(transcript);
            db.SaveChanges();
            var segment = new Segment { TranscriptId = transcript.Id, OrdinalIndex = 0, StartChar = 0, EndChar = 10, Text = "seg", CreatedAtUtc = createdAt };
            db.Segments.Add(segment);
            db.SaveChanges();
            var idea = new Idea { SegmentId = segment.Id, Premise = premise, CorrelationId = "c", CreatedAtUtc = createdAt };
            db.Ideas.Add(idea);
            db.SaveChanges();
            return idea.Id;
        }

        private void AddScore(int ideaId, double novelty, bool passed, DateTime createdAt)
        {
            using var db = NewDb();
            db.IdeaScores.Add(new IdeaScore
            {
                IdeaId = ideaId,
                Novelty = novelty,
                Coherence = novelty,
                SciFiPotential = novelty,
                FormulaFit = novelty,
                PassedThreshold = passed,
                Slot = ModelSlot.Scoring,
                Model = "m",
                PromptVersion = "v1",
                CreatedAtUtc = createdAt,
            });
            db.SaveChanges();
        }

        [Fact]
        public async Task IdeaRowsAsync_uses_latest_score_when_idea_rescored()
        {
            var ideaId = SeedVideoIdea("rescored idea", Now);
            AddScore(ideaId, novelty: 0.20, passed: false, Now);              // older
            AddScore(ideaId, novelty: 0.90, passed: true, Now.AddMinutes(5)); // newer (higher Id)

            using var db = NewDb();
            var rows = await new BrowseQueries(db).IdeaRowsAsync();

            var row = Assert.Single(rows);
            Assert.Equal(0.90, row.Novelty);
            Assert.True(row.Passed);
            Assert.Equal(0.90, row.Mean!.Value, precision: 6);
        }

        [Fact]
        public async Task IdeaRowsAsync_pasted_transcript_idea_has_null_video()
        {
            SeedVideoIdea("pasted idea", Now, videoUrl: null);

            using var db = NewDb();
            var rows = await new BrowseQueries(db).IdeaRowsAsync();

            var row = Assert.Single(rows);
            Assert.Null(row.VideoId);
            Assert.Null(row.VideoUrl);
            Assert.Equal("Transcript title", row.VideoTitle); // falls back to the transcript title
        }

        [Fact]
        public async Task IdeaRowsAsync_unscored_idea_has_null_passed_and_scores()
        {
            SeedVideoIdea("unscored idea", Now);

            using var db = NewDb();
            var rows = await new BrowseQueries(db).IdeaRowsAsync();

            var row = Assert.Single(rows);
            Assert.Null(row.Passed);
            Assert.Null(row.Novelty);
            Assert.Null(row.Mean);
        }

        [Fact]
        public async Task IdeaRowsAsync_orders_newest_first()
        {
            var older = SeedVideoIdea("older", Now.AddDays(-2));
            var newer = SeedVideoIdea("newer", Now);

            using var db = NewDb();
            var rows = await new BrowseQueries(db).IdeaRowsAsync();

            Assert.Equal(2, rows.Count);
            Assert.Equal(newer, rows[0].Id);
            Assert.Equal(older, rows[1].Id);
        }

        public void Dispose() => _connection.Dispose();
    }
}
