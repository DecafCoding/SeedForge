using Microsoft.Extensions.Logging.Abstractions;
using SeedForge.Data;
using SeedForge.Domain;
using SeedForge.Features.Concepts;
using SeedForge.Features.Config;
using SeedForge.UnitTests.Fakes;

namespace SeedForge.UnitTests
{
    /// <summary>Proves run-now regeneration appends a new active concept with the override's model and re-runs nothing upstream.</summary>
    public sealed class RegenerateConceptTests : IDisposable
    {
        private readonly SliceTestHarness _h = new();

        private static FakeLlmClient FakeWith(string title) => new FakeLlmClient().SetStructured(
            new BuildConceptResponse(title, "Sci-Fi", "Isolation", "A hook.", "A conflict.",
                "Will the captain save the crew against the AI?"));

        private BuildConceptHandler Build(FakeLlmClient fake, ApplicationDbContext db) =>
            new(db, fake, _h.Resolver, NullLogger<BuildConceptHandler>.Instance);

        private int SeedIdeaWithConcept()
        {
            var transcriptId = _h.SeedTranscript("text");
            var segmentId = _h.SeedSegment(transcriptId, "seg");
            var ideaId = _h.SeedIdea(segmentId, "a surviving premise", "corr-rc");
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
            db.SaveChanges();

            using var buildDb = _h.NewDb();
            Build(FakeWith("Original"), buildDb).HandleAsync(new(ideaId, "corr-rc"), default).GetAwaiter().GetResult();
            return ideaId;
        }

        [Fact]
        public async Task Regenerate_with_profile_override_appends_active_concept_and_flips_prior()
        {
            var ideaId = SeedIdeaWithConcept();

            // Seed profiles so a non-active profile override resolves to a distinct (OpenAI) model.
            int profileId;
            using (var seedDb = _h.NewDb())
            {
                await ProfileSeeder.SeedAsync(seedDb, ResolverHarness.RigOptions());
                profileId = seedDb.ConfigProfiles.Single(p => p.Name == ProfileSeeder.AllOpenAi).Id;
            }

            var fake = FakeWith("Regenerated");
            using (var db = _h.NewDb())
            {
                var handler = new RegenerateConceptHandler(Build(fake, db), _h.Resolver);
                await handler.HandleAsync(new(ideaId, profileId), default);
            }

            using var read = _h.NewDb();
            var concepts = read.Concepts.Where(c => c.IdeaId == ideaId).OrderBy(c => c.Id).ToList();
            Assert.Equal(2, concepts.Count);
            var active = Assert.Single(concepts, c => c.IsActive);
            Assert.Equal("Regenerated", active.Title);
            Assert.Equal(ProfileSeeder.DefaultOpenAiModel, active.Model); // recorded the override's model
            Assert.False(concepts.First().IsActive); // the prior was flipped off

            // No upstream re-run: still exactly one segment and one idea.
            Assert.Single(read.Segments);
            Assert.Single(read.Ideas);
        }

        public void Dispose() => _h.Dispose();
    }
}
