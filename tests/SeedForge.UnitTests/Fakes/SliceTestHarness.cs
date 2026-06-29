using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SeedForge.Data;
using SeedForge.Domain;
using SeedForge.Services.Ai;

namespace SeedForge.UnitTests.Fakes
{
    /// <summary>
    /// Shared scaffolding for slice tests: a SQLite-in-memory <see cref="ApplicationDbContext"/> (schema created
    /// from the model) and a <see cref="LlmOptionsResolver"/> with every <see cref="ModelSlot"/> configured.
    /// Keep the harness for the lifetime of a test so the in-memory connection stays open.
    /// </summary>
    public sealed class SliceTestHarness : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly DbContextOptions<ApplicationDbContext> _dbOptions;
        private readonly ApplicationDbContext _resolverDb;

        public SliceTestHarness()
        {
            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();
            _dbOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(_connection)
                .Options;

            using var db = NewDb();
            db.Database.EnsureCreated();

            var aiOptions = new AiOptions();
            foreach (var slot in Enum.GetValues<ModelSlot>())
            {
                aiOptions.Slots[slot.ToString()] =
                    new LlmOptions { BaseUrl = "http://rig", ApiKey = "local", Model = $"{slot}-model" };
            }
            // No profiles seeded ⇒ the resolver falls back to these appsettings, preserving Phase 2 behavior.
            _resolverDb = NewDb();
            Resolver = new LlmOptionsResolver(
                _resolverDb, new TestOptionsMonitor<AiOptions>(aiOptions), new ConfigurationBuilder().Build());
        }

        public LlmOptionsResolver Resolver { get; }

        /// <summary>Opens a fresh context over the shared in-memory database.</summary>
        public ApplicationDbContext NewDb() => new(_dbOptions);

        /// <summary>Inserts a Transcript (video-less) and returns its id.</summary>
        public int SeedTranscript(string plainText, string? title = null)
        {
            using var db = NewDb();
            var transcript = new Transcript { PlainText = plainText, Title = title, CreatedAtUtc = DateTime.UtcNow };
            db.Transcripts.Add(transcript);
            db.SaveChanges();
            return transcript.Id;
        }

        /// <summary>Inserts a Segment under a transcript and returns its id.</summary>
        public int SeedSegment(int transcriptId, string text)
        {
            using var db = NewDb();
            var segment = new Segment
            {
                TranscriptId = transcriptId,
                OrdinalIndex = 0,
                StartChar = 0,
                EndChar = text.Length,
                Text = text,
                CreatedAtUtc = DateTime.UtcNow,
            };
            db.Segments.Add(segment);
            db.SaveChanges();
            return segment.Id;
        }

        /// <summary>Inserts an Idea under a segment and returns its id.</summary>
        public int SeedIdea(int segmentId, string premise, string correlationId)
        {
            using var db = NewDb();
            var idea = new Idea
            {
                SegmentId = segmentId,
                Premise = premise,
                CorrelationId = correlationId,
                ExtractionPromptVersion = "v1",
                CreatedAtUtc = DateTime.UtcNow,
            };
            db.Ideas.Add(idea);
            db.SaveChanges();
            return idea.Id;
        }

        public void Dispose()
        {
            _resolverDb.Dispose();
            _connection.Dispose();
        }
    }
}
