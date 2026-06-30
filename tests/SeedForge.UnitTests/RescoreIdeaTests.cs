using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SeedForge.Data;
using SeedForge.Domain;
using SeedForge.Features;
using SeedForge.Features.Scoring;
using SeedForge.UnitTests.Fakes;

namespace SeedForge.UnitTests
{
    /// <summary>Proves re-scoring appends a new IdeaScore and applies the stale-not-deleted cascade to the idea's concepts.</summary>
    public sealed class RescoreIdeaTests : IDisposable
    {
        private readonly SliceTestHarness _h = new();

        private ScoreIdeasHandler Scorer(FakeLlmClient fake, ApplicationDbContext db, double threshold = 0.6) =>
            new(db, fake, _h.Resolver, Options.Create(new PipelineOptions { ScoreThreshold = threshold }),
                NullLogger<ScoreIdeasHandler>.Instance);

        private static FakeLlmClient FakeScore(double axis) => new FakeLlmClient().SetStructured(
            new ScoreIdeasResponse(new() { new IdeaScoreDto(0, axis, axis, axis, axis) }));

        /// <summary>Seeds an idea with one prior (passing) score and one active concept; returns the idea id.</summary>
        private int SeedScoredIdeaWithConcept()
        {
            var transcriptId = _h.SeedTranscript("text");
            var segmentId = _h.SeedSegment(transcriptId, "seg");
            var ideaId = _h.SeedIdea(segmentId, "a premise", "corr-rs");
            using var db = _h.NewDb();
            db.IdeaScores.Add(new IdeaScore
            {
                IdeaId = ideaId,
                Novelty = 0.9,
                Coherence = 0.9,
                Potential = 0.9,
                Suitability = 0.9,
                PassedThreshold = true,
                Slot = ModelSlot.Scoring,
                Model = "Scoring-model",
                PromptVersion = "v1",
                CreatedAtUtc = DateTime.UtcNow,
            });
            db.Concepts.Add(new Concept
            {
                IdeaId = ideaId,
                Title = "C",
                Slot = ModelSlot.Concept,
                Model = "Concept-model",
                PromptVersion = "v1",
                IsActive = true,
                IsStale = false,
                CreatedAtUtc = DateTime.UtcNow,
            });
            db.SaveChanges();
            return ideaId;
        }

        [Fact]
        public async Task Rescore_appends_a_new_score_keeping_prior_rows()
        {
            var ideaId = SeedScoredIdeaWithConcept();
            using (var db = _h.NewDb())
            {
                await new RescoreIdeaHandler(db, Scorer(FakeScore(0.9), db)).HandleAsync(new(ideaId), default);
            }

            using var read = _h.NewDb();
            Assert.Equal(2, read.IdeaScores.Count(s => s.IdeaId == ideaId)); // prior kept + new appended
        }

        [Fact]
        public async Task Below_threshold_rescore_flags_concepts_stale_without_deleting()
        {
            var ideaId = SeedScoredIdeaWithConcept();
            using (var db = _h.NewDb())
            {
                await new RescoreIdeaHandler(db, Scorer(FakeScore(0.2), db)).HandleAsync(new(ideaId), default);
            }

            using var read = _h.NewDb();
            var concepts = read.Concepts.Where(c => c.IdeaId == ideaId).ToList();
            Assert.Single(concepts);          // never deleted
            Assert.True(concepts[0].IsStale); // flagged stale on the drop below threshold
        }

        [Fact]
        public async Task Above_threshold_rescore_clears_stale()
        {
            var ideaId = SeedScoredIdeaWithConcept();

            // Mark the concept stale first to prove a passing re-score clears it.
            using (var db = _h.NewDb())
            {
                var c = db.Concepts.Single(c => c.IdeaId == ideaId);
                c.IsStale = true;
                db.SaveChanges();
            }
            using (var db = _h.NewDb())
            {
                await new RescoreIdeaHandler(db, Scorer(FakeScore(0.9), db)).HandleAsync(new(ideaId), default);
            }

            using var read = _h.NewDb();
            Assert.False(read.Concepts.Single(c => c.IdeaId == ideaId).IsStale);
        }

        public void Dispose() => _h.Dispose();
    }
}
