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
                Potential = novelty,
                Suitability = novelty,
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

        // ---- VideoDetailAsync ----

        private void AddCall(string correlationId, string stage, int totalTokens, double cost, DateTime createdAt, bool success = true)
        {
            using var db = NewDb();
            db.AiCallLogs.Add(new AiCallLog
            {
                CorrelationId = correlationId,
                Stage = stage,
                Slot = ModelSlot.Extraction,
                Model = "m",
                TotalTokens = totalTokens,
                EstimatedCost = cost,
                Success = success,
                CreatedAtUtc = createdAt,
            });
            db.SaveChanges();
        }

        [Fact]
        public async Task VideoDetailAsync_returns_null_for_unknown_id()
        {
            using var db = NewDb();
            Assert.Null(await new BrowseQueries(db).VideoDetailAsync(999_999));
        }

        [Fact]
        public async Task VideoDetailAsync_returns_full_graph_counts_and_concepts()
        {
            var passed1 = SeedVideoIdea("p1", Now);
            int videoId = VideoIdForIdea(passed1);
            AddScore(passed1, novelty: 0.9, passed: true, Now);
            var passed2 = SeedVideoIdeaForVideo("p2", videoId, Now);
            AddScore(passed2, novelty: 0.8, passed: true, Now);
            var failed1 = SeedVideoIdeaForVideo("f1", videoId, Now);
            AddScore(failed1, novelty: 0.1, passed: false, Now);
            var unscored1 = SeedVideoIdeaForVideo("u1", videoId, Now); // no score

            AddConcept(passed1, isActive: false, Now);
            AddConcept(passed1, isActive: true, Now.AddMinutes(1));

            using var db = NewDb();
            var d = await new BrowseQueries(db).VideoDetailAsync(videoId);

            Assert.NotNull(d);
            Assert.Equal(videoId, d!.Id);
            Assert.Equal(4, d.SegmentCount);   // one segment per seeded idea
            Assert.Equal(4, d.IdeaCount);
            Assert.Equal(2, d.Passed);
            Assert.Equal(1, d.Failed);
            Assert.Equal(1, d.Unscored);
            Assert.Equal(2, d.Concepts.Count);
            Assert.Contains(d.Concepts, c => c.IsActive);
        }

        [Fact]
        public async Task VideoDetailAsync_parses_clock_duration_from_raw_json()
        {
            int ideaId = SeedVideoIdea("clock", Now);
            int videoId = VideoIdForIdea(ideaId);
            using (var db = NewDb())
            {
                var t = db.Transcripts.First(t => t.VideoId == videoId);
                t.RawDatasetItemJson = """{ "duration": "1:02:03" }""";
                db.SaveChanges();
            }

            using var db2 = NewDb();
            var d = await new BrowseQueries(db2).VideoDetailAsync(videoId);

            Assert.Equal(3723, d!.DurationSeconds); // 1*3600 + 2*60 + 3
        }

        [Fact]
        public async Task VideoDetailAsync_parses_numeric_seconds_duration_from_raw_json()
        {
            int ideaId = SeedVideoIdea("secs", Now);
            int videoId = VideoIdForIdea(ideaId);
            using (var db = NewDb())
            {
                var t = db.Transcripts.First(t => t.VideoId == videoId);
                t.RawDatasetItemJson = """{ "lengthSeconds": 142 }""";
                db.SaveChanges();
            }

            using var db2 = NewDb();
            var d = await new BrowseQueries(db2).VideoDetailAsync(videoId);

            Assert.Equal(142, d!.DurationSeconds);
        }

        [Fact]
        public async Task VideoDetailAsync_prefers_stored_DurationSeconds_over_raw_json()
        {
            int ideaId = SeedVideoIdea("stored-dur", Now);
            int videoId = VideoIdForIdea(ideaId);
            using (var db = NewDb())
            {
                var v = db.Videos.First(v => v.Id == videoId);
                v.DurationSeconds = 999;
                var t = db.Transcripts.First(t => t.VideoId == videoId);
                t.RawDatasetItemJson = """{ "duration": "1:02:03" }""";
                db.SaveChanges();
            }

            using var db2 = NewDb();
            var d = await new BrowseQueries(db2).VideoDetailAsync(videoId);

            Assert.Equal(999, d!.DurationSeconds); // Phase 8 column wins
        }

        [Fact]
        public async Task VideoDetailAsync_duration_null_when_neither_source_has_it()
        {
            int ideaId = SeedVideoIdea("no-dur", Now);
            int videoId = VideoIdForIdea(ideaId); // raw json defaults to "" — no duration key

            using var db = NewDb();
            var d = await new BrowseQueries(db).VideoDetailAsync(videoId);

            Assert.Null(d!.DurationSeconds);
        }

        [Fact]
        public async Task VideoDetailAsync_derives_processed_time_from_latest_ai_call()
        {
            // Seed an idea with a known correlation id, then two calls; the latest call time wins.
            int videoId;
            using (var db = NewDb())
            {
                var video = new Video { YouTubeVideoId = "yt-trace", Url = "https://youtu.be/trace", Title = "Traced", Status = VideoJobStatus.Done, CreatedAtUtc = Now };
                db.Videos.Add(video);
                db.SaveChanges();
                videoId = video.Id;
                var transcript = new Transcript { VideoId = videoId, PlainText = "t", CreatedAtUtc = Now };
                db.Transcripts.Add(transcript);
                db.SaveChanges();
                var segment = new Segment { TranscriptId = transcript.Id, OrdinalIndex = 0, StartChar = 0, EndChar = 1, Text = "s", CreatedAtUtc = Now };
                db.Segments.Add(segment);
                db.SaveChanges();
                db.Ideas.Add(new Idea { SegmentId = segment.Id, Premise = "p", CorrelationId = "corr-1", CreatedAtUtc = Now });
                db.SaveChanges();
            }
            AddCall("corr-1", "Extraction", totalTokens: 10, cost: 0.01, Now.AddMinutes(2));
            var latest = Now.AddMinutes(9);
            AddCall("corr-1", "Concept", totalTokens: 20, cost: 0.02, latest);

            using var db2 = NewDb();
            var d = await new BrowseQueries(db2).VideoDetailAsync(videoId);

            Assert.True(d!.IsProcessedDerived);
            Assert.Equal(latest, d.DateProcessedUtc);
            Assert.Equal(2, d.AiCalls.Count);
            Assert.Equal(30, d.TotalTokens);
            Assert.Equal(0.03, d.TotalCost, precision: 6);
        }

        [Fact]
        public async Task VideoDetailAsync_prefers_stored_processed_time_and_clears_derived_flag()
        {
            var stored = Now.AddMinutes(42);
            int ideaId = SeedVideoIdea("stored-proc", Now);
            int videoId = VideoIdForIdea(ideaId);
            using (var db = NewDb())
            {
                var v = db.Videos.First(v => v.Id == videoId);
                v.ProcessedAtUtc = stored;
                db.SaveChanges();
            }

            using var db2 = NewDb();
            var d = await new BrowseQueries(db2).VideoDetailAsync(videoId);

            Assert.False(d!.IsProcessedDerived);
            Assert.Equal(stored, d.DateProcessedUtc);
        }

        [Fact]
        public async Task VideoDetailAsync_falls_back_to_transcript_time_when_no_ai_calls()
        {
            var transcriptTime = Now.AddHours(3);
            int ideaId = SeedVideoIdea("no-calls", transcriptTime);
            int videoId = VideoIdForIdea(ideaId);

            using var db = NewDb();
            var d = await new BrowseQueries(db).VideoDetailAsync(videoId);

            Assert.True(d!.IsProcessedDerived);
            Assert.Equal(transcriptTime, d.DateProcessedUtc);
            Assert.Empty(d.AiCalls);
        }

        [Fact]
        public async Task VideoDetailAsync_resolves_channel_via_channel_then_transcript_fallback()
        {
            // Video tied to a Channel row resolves the channel title.
            int channelId;
            int videoWithChannel;
            using (var db = NewDb())
            {
                var ch = new Channel { YouTubeChannelId = "UCxyz", Title = "Cosmic Channel", UploadsPlaylistId = "UUxyz", AddedAtUtc = Now };
                db.Channels.Add(ch);
                db.SaveChanges();
                channelId = ch.Id;
                var video = new Video { YouTubeVideoId = "yt-ch", ChannelId = channelId, Url = "https://youtu.be/ch", Status = VideoJobStatus.Done, CreatedAtUtc = Now };
                db.Videos.Add(video);
                db.SaveChanges();
                videoWithChannel = video.Id;
                db.Transcripts.Add(new Transcript { VideoId = videoWithChannel, PlainText = "t", ChannelName = "Raw Name", CreatedAtUtc = Now });
                db.SaveChanges();
            }

            using var db2 = NewDb();
            var d = await new BrowseQueries(db2).VideoDetailAsync(videoWithChannel);
            Assert.Equal("Cosmic Channel", d!.Channel); // Channel row wins

            // A channel-less video falls back to the transcript's channel name.
            int videoNoChannel;
            using (var db = NewDb())
            {
                var video = new Video { YouTubeVideoId = "yt-noch", Url = "https://youtu.be/noch", Status = VideoJobStatus.Done, CreatedAtUtc = Now };
                db.Videos.Add(video);
                db.SaveChanges();
                videoNoChannel = video.Id;
                db.Transcripts.Add(new Transcript { VideoId = videoNoChannel, PlainText = "t", ChannelName = "Fallback Name", CreatedAtUtc = Now });
                db.SaveChanges();
            }

            using var db3 = NewDb();
            var d2 = await new BrowseQueries(db3).VideoDetailAsync(videoNoChannel);
            Assert.Equal("Fallback Name", d2!.Channel);
        }

        [Fact]
        public async Task VideoDetailAsync_transcriptless_video_renders_identity_with_empty_yield()
        {
            int videoId;
            using (var db = NewDb())
            {
                var video = new Video { YouTubeVideoId = "yt-none", Url = "https://youtu.be/none", Title = "Dud", Status = VideoJobStatus.NoTranscript, CreatedAtUtc = Now };
                db.Videos.Add(video);
                db.SaveChanges();
                videoId = video.Id;
            }

            using var db2 = NewDb();
            var d = await new BrowseQueries(db2).VideoDetailAsync(videoId);

            Assert.NotNull(d);
            Assert.Equal("Dud", d!.Title);
            Assert.Equal(VideoJobStatus.NoTranscript, d.Status);
            Assert.Equal(0, d.SegmentCount);
            Assert.Equal(0, d.IdeaCount);
            Assert.Null(d.DurationSeconds);
            Assert.Null(d.DateProcessedUtc);
            Assert.Empty(d.Concepts);
            Assert.Empty(d.AiCalls);
        }

        public void Dispose() => _connection.Dispose();
    }
}
