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

        /// <summary>
        /// Seeds an additional Segment → Idea under the existing video's single transcript and returns the idea id.
        /// (Transcript.VideoId is uniquely indexed — one transcript per video — so extra ideas share the transcript.)
        /// </summary>
        private int SeedVideoIdeaForVideo(string premise, int videoId, DateTime createdAt)
        {
            using var db = NewDb();
            var transcript = db.Transcripts.First(t => t.VideoId == videoId);
            var segment = new Segment { TranscriptId = transcript.Id, OrdinalIndex = 1, StartChar = 0, EndChar = 10, Text = "seg", CreatedAtUtc = createdAt };
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

        /// <summary>Returns the video id owning the transcript of <paramref name="ideaId"/> (helper for concept seeding).</summary>
        private int VideoIdForIdea(int ideaId)
        {
            using var db = NewDb();
            var segId = db.Ideas.First(i => i.Id == ideaId).SegmentId;
            var transcriptId = db.Segments.First(s => s.Id == segId).TranscriptId;
            return db.Transcripts.First(t => t.Id == transcriptId).VideoId!.Value;
        }

        private void AddConcept(int ideaId, bool isActive, DateTime createdAt)
        {
            using var db = NewDb();
            db.Concepts.Add(new Concept
            {
                IdeaId = ideaId,
                Title = "A concept",
                Slot = ModelSlot.Concept,
                Model = "m",
                PromptVersion = "v1",
                IsActive = isActive,
                CreatedAtUtc = createdAt,
            });
            db.SaveChanges();
        }

        [Fact]
        public async Task VideoRowsAsync_splits_ideas_by_latest_score_and_counts_active_concepts()
        {
            // One video, three ideas: two pass on their latest score, one fails.
            var passed1 = SeedVideoIdea("p1", Now);
            int videoId = VideoIdForIdea(passed1);
            AddScore(passed1, novelty: 0.9, passed: true, Now);
            var passed2 = SeedVideoIdeaForVideo("p2", videoId, Now);
            AddScore(passed2, novelty: 0.8, passed: true, Now);
            var failed1 = SeedVideoIdeaForVideo("f1", videoId, Now);
            AddScore(failed1, novelty: 0.1, passed: false, Now);

            // Two concepts on passed1: one active, one stale/inactive ⇒ total 2, active 1.
            AddConcept(passed1, isActive: false, Now);
            AddConcept(passed1, isActive: true, Now.AddMinutes(1));

            using var db = NewDb();
            var rows = await new BrowseQueries(db).VideoRowsAsync();

            var row = Assert.Single(rows);
            Assert.Equal(videoId, row.Id);
            Assert.Equal(3, row.IdeaCount);
            Assert.Equal(2, row.Passed);
            Assert.Equal(1, row.Failed);
            Assert.Equal(0, row.Unscored);
            Assert.Equal(2, row.ConceptCount);
            Assert.Equal(1, row.ActiveConceptCount);
        }

        [Fact]
        public async Task VideoRowsAsync_uses_latest_verdict_when_idea_rescored()
        {
            var ideaId = SeedVideoIdea("rescored", Now);
            int videoId = VideoIdForIdea(ideaId);
            AddScore(ideaId, novelty: 0.9, passed: true, Now);                 // older — passed
            AddScore(ideaId, novelty: 0.1, passed: false, Now.AddMinutes(5));  // newer — failed

            using var db = NewDb();
            var row = Assert.Single(await new BrowseQueries(db).VideoRowsAsync());

            Assert.Equal(0, row.Passed);
            Assert.Equal(1, row.Failed);
            Assert.Equal(0, row.Unscored);
        }

        [Fact]
        public async Task VideoRowsAsync_no_transcript_video_yields_zeros_with_status()
        {
            using (var db = NewDb())
            {
                db.Videos.Add(new Video
                {
                    YouTubeVideoId = "yt-none",
                    Url = "https://youtu.be/none",
                    Title = "Dud",
                    Status = VideoJobStatus.NoTranscript,
                    CreatedAtUtc = Now,
                });
                db.SaveChanges();
            }

            using var db2 = NewDb();
            var row = Assert.Single(await new BrowseQueries(db2).VideoRowsAsync());

            Assert.Equal(VideoJobStatus.NoTranscript, row.Status);
            Assert.Equal(0, row.IdeaCount);
            Assert.Equal(0, row.Passed);
            Assert.Equal(0, row.Failed);
            Assert.Equal(0, row.Unscored);
            Assert.Equal(0, row.ConceptCount);
            Assert.Equal(0, row.ActiveConceptCount);
        }

        [Fact]
        public async Task VideoRowsAsync_counts_unscored_ideas_distinctly()
        {
            var ideaId = SeedVideoIdea("never scored", Now); // no AddScore

            using var db = NewDb();
            var row = Assert.Single(await new BrowseQueries(db).VideoRowsAsync());

            Assert.Equal(1, row.IdeaCount);
            Assert.Equal(0, row.Passed);
            Assert.Equal(0, row.Failed);
            Assert.Equal(1, row.Unscored);
        }

        [Fact]
        public async Task VideoRowsAsync_orders_newest_first()
        {
            SeedVideoIdea("older", Now.AddDays(-2), videoUrl: "https://youtu.be/old");
            SeedVideoIdea("newer", Now, videoUrl: "https://youtu.be/new");

            using var db = NewDb();
            var rows = await new BrowseQueries(db).VideoRowsAsync();

            Assert.Equal(2, rows.Count);
            Assert.True(rows[0].Id > rows[1].Id); // newest (highest Id) first
        }

        public void Dispose() => _connection.Dispose();
    }
}
