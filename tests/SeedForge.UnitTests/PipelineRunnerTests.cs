using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SeedForge.Data;
using SeedForge.Features;
using SeedForge.Features.Concepts;
using SeedForge.Features.Extraction;
using SeedForge.Features.Scoring;
using SeedForge.Features.Segmentation;
using SeedForge.Pipeline;
using SeedForge.UnitTests.Fakes;

namespace SeedForge.UnitTests
{
    /// <summary>End-to-end: the runner composes the four slices into a complete, survivors-only lineage under one correlation id.</summary>
    public class PipelineRunnerTests : IDisposable
    {
        private readonly SliceTestHarness _h = new();

        private PipelineRunner BuildRunner(FakeLlmClient fake, ApplicationDbContext db)
        {
            var options = Options.Create(new PipelineOptions { ScoreThreshold = 0.6 });
            return new PipelineRunner(
                db,
                new SegmentTranscriptHandler(db, fake, _h.Resolver, NullLogger<SegmentTranscriptHandler>.Instance),
                new ExtractIdeasHandler(db, fake, _h.Resolver, NullLogger<ExtractIdeasHandler>.Instance),
                new ScoreIdeasHandler(db, fake, _h.Resolver, options, NullLogger<ScoreIdeasHandler>.Instance),
                new BuildConceptHandler(db, fake, _h.Resolver, NullLogger<BuildConceptHandler>.Instance),
                NullLogger<PipelineRunner>.Instance);
        }

        [Fact]
        public async Task RunAsync_produces_full_lineage_with_concepts_for_survivors_only()
        {
            const string text = "First we cover terraforming Mars over generations. " +
                                 "Then we explore a rogue AI that runs a starship alone.";

            // One idea per segment (2 ideas); one survives scoring, one is culled ⇒ exactly one concept.
            var fake = new FakeLlmClient()
                .SetStructured(new SegmentationResponse(new()
                {
                    new SegmentBoundaryDto(0, "Terraforming", "First we cover terraforming Mars"),
                    new SegmentBoundaryDto(1, "Rogue AI", "Then we explore a rogue AI"),
                }))
                .SetStructured(new ExtractIdeasResponse(new() { new ThinIdeaDto("A premise from this segment.") }))
                .SetStructured(new ScoreIdeasResponse(new()
                {
                    new IdeaScoreDto(0, 0.9, 0.9, 0.9, 0.9), // survives
                    new IdeaScoreDto(1, 0.1, 0.1, 0.1, 0.1), // culled
                }))
                .SetStructured(new BuildConceptResponse(
                    "Title", "Sci-Fi", "Theme", "Hook", "Conflict", "Will X achieve Y against Z?"));

            using var db = _h.NewDb();
            var result = await BuildRunner(fake, db).RunAsync(text, "My Transcript", default);

            using var read = _h.NewDb();

            // Transcript is video-less and persisted.
            var transcript = read.Transcripts.Single(t => t.Id == result.TranscriptId);
            Assert.Null(transcript.VideoId);
            Assert.Equal("My Transcript", transcript.Title);

            // Two segments, two ideas, one survivor, one concept.
            Assert.Equal(2, result.SegmentIds.Count);
            Assert.Equal(2, result.IdeaIds.Count);
            Assert.Single(result.SurvivorIds);
            Assert.Single(result.ConceptIds);

            // IdeaScores come from one scoring pass: two rows.
            Assert.Equal(2, read.IdeaScores.Count(s => result.IdeaIds.Contains(s.IdeaId)));

            // The single concept belongs to the survivor and is active.
            var concept = read.Concepts.Single(c => c.Id == result.ConceptIds[0]);
            Assert.True(concept.IsActive);
            Assert.Equal(result.SurvivorIds[0], concept.IdeaId);

            // Every AI call shares the one correlation id, and the run's derivatives carry it.
            Assert.All(fake.Contexts, c => Assert.Equal(result.CorrelationId, c.CorrelationId));
            Assert.All(read.Ideas.Where(i => result.IdeaIds.Contains(i.Id)),
                i => Assert.Equal(result.CorrelationId, i.CorrelationId));
            // Call count: 1 segmentation + 2 extraction + 1 scoring + 1 concept = 5.
            Assert.Equal(5, fake.Contexts.Count);
        }

        public void Dispose() => _h.Dispose();
    }
}
