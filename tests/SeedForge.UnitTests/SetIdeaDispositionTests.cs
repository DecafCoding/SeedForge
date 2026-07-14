using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SeedForge.Data;
using SeedForge.Domain;
using SeedForge.Features.Disposition;
using SeedForge.Services.Queues;
using SeedForge.Workers;

namespace SeedForge.UnitTests
{
    /// <summary>
    /// The manual Keep/Skip gate: Keep idempotently enqueues an Auto ConceptJob (even for a failed score),
    /// Skip cancels pending jobs but never touches started work or existing concepts.
    /// </summary>
    public sealed class SetIdeaDispositionTests : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly DbContextOptions<ApplicationDbContext> _dbOptions;
        private static readonly DateTime Now = new(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

        public SetIdeaDispositionTests()
        {
            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();
            _dbOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(_connection).Options;
            using var db = NewDb();
            db.Database.EnsureCreated();
        }

        private ApplicationDbContext NewDb() => new(_dbOptions);

        private SetIdeaDispositionHandler NewHandler(ApplicationDbContext db)
        {
            var queue = new ConceptQueue(db, Options.Create(new WorkerOptions()), new WorkerControl(),
                NullLogger<ConceptQueue>.Instance);
            return new SetIdeaDispositionHandler(db, queue, NullLogger<SetIdeaDispositionHandler>.Instance);
        }

        /// <summary>Seeds a Transcript → Segment → Idea lineage (no video) and returns the idea id.</summary>
        private int SeedIdea()
        {
            using var db = NewDb();
            var transcript = new Transcript { VideoId = null, PlainText = "text", CreatedAtUtc = Now };
            db.Transcripts.Add(transcript);
            db.SaveChanges();
            var segment = new Segment { TranscriptId = transcript.Id, OrdinalIndex = 0, StartChar = 0, EndChar = 10, Text = "seg", CreatedAtUtc = Now };
            db.Segments.Add(segment);
            db.SaveChanges();
            var idea = new Idea { SegmentId = segment.Id, Premise = "a premise", CorrelationId = "c", CreatedAtUtc = Now };
            db.Ideas.Add(idea);
            db.SaveChanges();
            return idea.Id;
        }

        private void AddFailedScore(int ideaId)
        {
            using var db = NewDb();
            db.IdeaScores.Add(new IdeaScore
            {
                IdeaId = ideaId, Novelty = 0.1, Coherence = 0.1, Potential = 0.1, Suitability = 0.1,
                PassedThreshold = false, Slot = ModelSlot.Scoring, Model = "m", PromptVersion = "v1", CreatedAtUtc = Now,
            });
            db.SaveChanges();
        }

        private void AddConcept(int ideaId)
        {
            using var db = NewDb();
            db.Concepts.Add(new Concept
            {
                IdeaId = ideaId, Title = "T", Genre = "G", Theme = "Th", Hook = "H", Conflict = "C",
                CentralStoryQuestion = "Q", Slot = ModelSlot.Concept, Model = "m", PromptVersion = "v1",
                IsActive = true, CreatedAtUtc = Now,
            });
            db.SaveChanges();
        }

        private void AddJob(int ideaId, ConceptJobStatus status)
        {
            using var db = NewDb();
            db.ConceptJobs.Add(new ConceptJob
            {
                IdeaId = ideaId, Status = status, Trigger = ConceptTrigger.Auto, CreatedAtUtc = Now,
            });
            db.SaveChanges();
        }

        [Fact]
        public async Task HandleAsync_Keep_EnqueuesPendingAutoJobAndStampsIdea()
        {
            var ideaId = SeedIdea();

            using var db = NewDb();
            await NewHandler(db).HandleAsync(new(ideaId, IdeaDisposition.Keep), default);

            using var read = NewDb();
            var idea = read.Ideas.Single(i => i.Id == ideaId);
            Assert.Equal(IdeaDisposition.Keep, idea.Disposition);
            Assert.NotNull(idea.DispositionAtUtc);
            var job = Assert.Single(read.ConceptJobs);
            Assert.Equal(ideaId, job.IdeaId);
            Assert.Equal(ConceptJobStatus.Pending, job.Status);
            Assert.Equal(ConceptTrigger.Auto, job.Trigger);
        }

        [Fact]
        public async Task HandleAsync_KeepOnFailedIdea_StillEnqueues()
        {
            var ideaId = SeedIdea();
            AddFailedScore(ideaId); // the rescue path: pass/fail is a signal, not a gate

            using var db = NewDb();
            await NewHandler(db).HandleAsync(new(ideaId, IdeaDisposition.Keep), default);

            using var read = NewDb();
            Assert.Single(read.ConceptJobs.Where(j => j.IdeaId == ideaId && j.Status == ConceptJobStatus.Pending));
        }

        [Fact]
        public async Task HandleAsync_KeepTwice_EnqueuesOnlyOnce()
        {
            var ideaId = SeedIdea();

            using var db = NewDb();
            var handler = NewHandler(db);
            await handler.HandleAsync(new(ideaId, IdeaDisposition.Keep), default);
            await handler.HandleAsync(new(ideaId, IdeaDisposition.Keep), default);

            using var read = NewDb();
            Assert.Single(read.ConceptJobs);
        }

        [Fact]
        public async Task HandleAsync_KeepWhenConceptExists_DoesNotEnqueue()
        {
            var ideaId = SeedIdea();
            AddConcept(ideaId);

            using var db = NewDb();
            await NewHandler(db).HandleAsync(new(ideaId, IdeaDisposition.Keep), default);

            using var read = NewDb();
            Assert.Equal(IdeaDisposition.Keep, read.Ideas.Single(i => i.Id == ideaId).Disposition);
            Assert.Empty(read.ConceptJobs);
        }

        [Fact]
        public async Task HandleAsync_KeepWhenOpenJobExists_DoesNotEnqueue()
        {
            var ideaId = SeedIdea();
            AddJob(ideaId, ConceptJobStatus.InProgress);

            using var db = NewDb();
            await NewHandler(db).HandleAsync(new(ideaId, IdeaDisposition.Keep), default);

            using var read = NewDb();
            Assert.Single(read.ConceptJobs); // only the pre-existing job
        }

        [Fact]
        public async Task HandleAsync_KeepAfterSkipWhenConceptExists_DoesNotRebuild()
        {
            var ideaId = SeedIdea();
            AddJob(ideaId, ConceptJobStatus.Done);
            AddConcept(ideaId);

            using var db = NewDb();
            var handler = NewHandler(db);
            await handler.HandleAsync(new(ideaId, IdeaDisposition.Skip), default);
            await handler.HandleAsync(new(ideaId, IdeaDisposition.Keep), default);

            using var read = NewDb();
            // Concept already exists ⇒ flipping back to Keep does not rebuild.
            Assert.Single(read.ConceptJobs.Where(j => j.Status == ConceptJobStatus.Done));
            Assert.Single(read.Concepts);
        }

        [Fact]
        public async Task HandleAsync_Skip_NeverEnqueuesAndStampsIdea()
        {
            var ideaId = SeedIdea();

            using var db = NewDb();
            await NewHandler(db).HandleAsync(new(ideaId, IdeaDisposition.Skip), default);

            using var read = NewDb();
            var idea = read.Ideas.Single(i => i.Id == ideaId);
            Assert.Equal(IdeaDisposition.Skip, idea.Disposition);
            Assert.NotNull(idea.DispositionAtUtc);
            Assert.Empty(read.ConceptJobs);
        }

        [Fact]
        public async Task HandleAsync_SkipWithPendingJob_DeletesPendingJob()
        {
            var ideaId = SeedIdea();
            AddJob(ideaId, ConceptJobStatus.Pending);

            using var db = NewDb();
            await NewHandler(db).HandleAsync(new(ideaId, IdeaDisposition.Skip), default);

            using var read = NewDb();
            Assert.Empty(read.ConceptJobs); // the not-yet-started build is cancelled
        }

        [Fact]
        public async Task HandleAsync_SkipWithInProgressJob_LeavesJobAlone()
        {
            var ideaId = SeedIdea();
            AddJob(ideaId, ConceptJobStatus.InProgress);

            using var db = NewDb();
            await NewHandler(db).HandleAsync(new(ideaId, IdeaDisposition.Skip), default);

            using var read = NewDb();
            Assert.Single(read.ConceptJobs.Where(j => j.Status == ConceptJobStatus.InProgress));
        }

        [Fact]
        public async Task HandleAsync_SkipAfterConceptExists_LeavesConceptIntact()
        {
            var ideaId = SeedIdea();
            AddConcept(ideaId);

            using var db = NewDb();
            await NewHandler(db).HandleAsync(new(ideaId, IdeaDisposition.Skip), default);

            using var read = NewDb();
            var concept = Assert.Single(read.Concepts);
            Assert.True(concept.IsActive); // stale-not-deleted: skipping never destroys built lineage
        }

        [Fact]
        public async Task HandleAsync_UnknownIdea_Throws()
        {
            using var db = NewDb();
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => NewHandler(db).HandleAsync(new(999, IdeaDisposition.Keep), default));
        }

        public void Dispose() => _connection.Dispose();
    }
}
