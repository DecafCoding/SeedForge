using Microsoft.Extensions.Logging.Abstractions;
using SeedForge.Data;
using SeedForge.Domain;
using SeedForge.Features.Concepts;
using SeedForge.UnitTests.Fakes;

namespace SeedForge.UnitTests
{
    /// <summary>Proves concept-building appends one active concept per idea with the score link, flipping priors off.</summary>
    public class BuildConceptTests : IDisposable
    {
        private readonly SliceTestHarness _h = new();

        private BuildConceptHandler Handler(FakeLlmClient fake, ApplicationDbContext db) =>
            new(db, fake, _h.Resolver, NullLogger<BuildConceptHandler>.Instance);

        private static FakeLlmClient FakeWith(string title) => new FakeLlmClient().SetStructured(
            new BuildConceptResponse(title, "Sci-Fi", "Isolation", "A hook.", "A conflict.",
                "Will the captain save the crew against the AI?"));

        private (int ideaId, int scoreId) SeedSurvivor()
        {
            var transcriptId = _h.SeedTranscript("text");
            var segmentId = _h.SeedSegment(transcriptId, "seg");
            var ideaId = _h.SeedIdea(segmentId, "a surviving premise", "corr-bc");
            using var db = _h.NewDb();
            var score = new IdeaScore
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
            };
            db.IdeaScores.Add(score);
            db.SaveChanges();
            return (ideaId, score.Id);
        }

        [Fact]
        public async Task Survivor_idea_yields_one_active_concept_with_fields_and_score_link()
        {
            var (ideaId, scoreId) = SeedSurvivor();
            var fake = FakeWith("First Light");

            using var db = _h.NewDb();
            var conceptId = await Handler(fake, db).HandleAsync(new(ideaId, "corr-bc"), default);

            using var read = _h.NewDb();
            var concept = read.Concepts.Single(c => c.Id == conceptId);
            Assert.True(concept.IsActive);
            Assert.Equal("First Light", concept.Title);
            Assert.Equal("Sci-Fi", concept.Genre);
            Assert.Equal("Isolation", concept.Theme);
            Assert.False(string.IsNullOrEmpty(concept.Hook));
            Assert.False(string.IsNullOrEmpty(concept.Conflict));
            Assert.False(string.IsNullOrEmpty(concept.CentralStoryQuestion));
            Assert.Equal("v1", concept.PromptVersion);
            Assert.Equal(scoreId, concept.IdeaScoreId);
            Assert.Equal(ModelSlot.Concept, Assert.Single(fake.Contexts).Slot);
        }

        [Fact]
        public async Task Second_build_flips_prior_inactive_leaving_exactly_one_active()
        {
            var (ideaId, _) = SeedSurvivor();

            using (var db1 = _h.NewDb())
            {
                await Handler(FakeWith("Draft One"), db1).HandleAsync(new(ideaId, "corr-bc"), default);
            }
            using (var db2 = _h.NewDb())
            {
                await Handler(FakeWith("Draft Two"), db2).HandleAsync(new(ideaId, "corr-bc"), default);
            }

            using var read = _h.NewDb();
            var concepts = read.Concepts.Where(c => c.IdeaId == ideaId).ToList();
            Assert.Equal(2, concepts.Count);
            var active = Assert.Single(concepts, c => c.IsActive);
            Assert.Equal("Draft Two", active.Title);
        }

        public void Dispose() => _h.Dispose();
    }
}
