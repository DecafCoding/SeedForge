using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SeedForge.Data;
using SeedForge.Domain;
using SeedForge.Features;
using SeedForge.Features.Scoring;
using SeedForge.UnitTests.Fakes;

namespace SeedForge.UnitTests
{
    /// <summary>Proves one scoring pass appends a score per idea and returns survivors by threshold.</summary>
    public class ScoreIdeasTests : IDisposable
    {
        private readonly SliceTestHarness _h = new();

        private ScoreIdeasHandler Handler(FakeLlmClient fake, ApplicationDbContext db, double threshold = 0.6) =>
            new(db, fake, _h.Resolver, Options.Create(new PipelineOptions { ScoreThreshold = threshold }),
                NullLogger<ScoreIdeasHandler>.Instance);

        [Fact]
        public async Task One_pass_scores_all_ideas_and_returns_threshold_survivors()
        {
            var transcriptId = _h.SeedTranscript("text");
            var segmentId = _h.SeedSegment(transcriptId, "seg");
            var id0 = _h.SeedIdea(segmentId, "premise zero", "corr-sc");
            var id1 = _h.SeedIdea(segmentId, "premise one", "corr-sc");
            var id2 = _h.SeedIdea(segmentId, "premise two", "corr-sc");

            var fake = new FakeLlmClient().SetStructured(new ScoreIdeasResponse(new()
            {
                new IdeaScoreDto(0, 0.9, 0.9, 0.9, 0.9), // mean 0.9 ⇒ survives
                new IdeaScoreDto(1, 0.2, 0.2, 0.2, 0.2), // mean 0.2 ⇒ culled
                new IdeaScoreDto(2, 0.7, 0.6, 0.6, 0.7), // mean 0.65 ⇒ survives
            }));

            using var db = _h.NewDb();
            var survivors = await Handler(fake, db).HandleAsync(new(new[] { id0, id1, id2 }, "corr-sc"), default);

            Assert.Equal(new[] { id0, id2 }, survivors);
            Assert.Single(fake.Contexts); // exactly one scoring call
            Assert.Equal(ModelSlot.Scoring, fake.Contexts[0].Slot);

            using var read = _h.NewDb();
            var scores = read.IdeaScores.Where(s => s.IdeaId == id0 || s.IdeaId == id1 || s.IdeaId == id2).ToList();
            Assert.Equal(3, scores.Count); // three rows from one pass
            Assert.True(scores.Single(s => s.IdeaId == id0).PassedThreshold);
            Assert.False(scores.Single(s => s.IdeaId == id1).PassedThreshold);
            Assert.True(scores.Single(s => s.IdeaId == id2).PassedThreshold);
            Assert.All(scores, s => Assert.Equal("v1", s.PromptVersion));
            Assert.All(scores, s => Assert.Equal(ModelSlot.Scoring, s.Slot));
        }

        [Fact]
        public async Task Empty_idea_list_makes_no_call_and_returns_no_survivors()
        {
            var fake = new FakeLlmClient();
            using var db = _h.NewDb();
            var survivors = await Handler(fake, db).HandleAsync(new(Array.Empty<int>(), "corr-empty"), default);

            Assert.Empty(survivors);
            Assert.Empty(fake.Contexts); // no scoring call when there are no ideas
        }

        [Fact]
        public async Task Mismatched_indices_score_only_matched_ideas()
        {
            var transcriptId = _h.SeedTranscript("text");
            var segmentId = _h.SeedSegment(transcriptId, "seg");
            var id0 = _h.SeedIdea(segmentId, "premise zero", "corr-mm");
            var id1 = _h.SeedIdea(segmentId, "premise one", "corr-mm");

            var fake = new FakeLlmClient().SetStructured(new ScoreIdeasResponse(new()
            {
                new IdeaScoreDto(0, 0.9, 0.9, 0.9, 0.9),  // valid
                new IdeaScoreDto(5, 0.9, 0.9, 0.9, 0.9),  // out of range ⇒ ignored
            }));

            using var db = _h.NewDb();
            var survivors = await Handler(fake, db).HandleAsync(new(new[] { id0, id1 }, "corr-mm"), default);

            Assert.Equal(new[] { id0 }, survivors);
            using var read = _h.NewDb();
            var scores = read.IdeaScores.Where(s => s.IdeaId == id0 || s.IdeaId == id1).ToList();
            Assert.Single(scores); // only the matched index produced a row
            Assert.Equal(id0, scores[0].IdeaId);
        }

        public void Dispose() => _h.Dispose();
    }
}
