using Microsoft.Extensions.Logging.Abstractions;
using SeedForge.Data;
using SeedForge.Domain;
using SeedForge.Features.Extraction;
using SeedForge.UnitTests.Fakes;

namespace SeedForge.UnitTests
{
    /// <summary>Proves extraction persists 0..N immutable ideas stamped with the correlation id and version.</summary>
    public class ExtractIdeasTests : IDisposable
    {
        private readonly SliceTestHarness _h = new();

        private ExtractIdeasHandler Handler(FakeLlmClient fake, ApplicationDbContext db) =>
            new(db, fake, _h.Resolver, NullLogger<ExtractIdeasHandler>.Instance);

        [Fact]
        public async Task Two_ideas_persist_two_rows_with_correlation_and_version()
        {
            var transcriptId = _h.SeedTranscript("text");
            var segmentId = _h.SeedSegment(transcriptId, "a segment about space");
            var fake = new FakeLlmClient().SetStructured(new ExtractIdeasResponse(new()
            {
                new ThinIdeaDto("A colony ship wakes its crew a century early."),
                new ThinIdeaDto("An AI negotiates peace by rewriting both sides' memories."),
            }));

            using var db = _h.NewDb();
            var ids = await Handler(fake, db).HandleAsync(new(segmentId, "corr-ex"), default);

            Assert.Equal(2, ids.Count);
            using var read = _h.NewDb();
            var ideas = read.Ideas.Where(i => i.SegmentId == segmentId).ToList();
            Assert.Equal(2, ideas.Count);
            Assert.All(ideas, i => Assert.Equal("corr-ex", i.CorrelationId));
            Assert.All(ideas, i => Assert.Equal("v1", i.ExtractionPromptVersion));
            Assert.Equal(ModelSlot.Extraction, Assert.Single(fake.Contexts).Slot);
        }

        [Fact]
        public async Task Zero_ideas_persists_no_rows_without_error()
        {
            var transcriptId = _h.SeedTranscript("text");
            var segmentId = _h.SeedSegment(transcriptId, "a segment with nothing usable");
            var fake = new FakeLlmClient().SetStructured(new ExtractIdeasResponse(new()));

            using var db = _h.NewDb();
            var ids = await Handler(fake, db).HandleAsync(new(segmentId, "corr-0"), default);

            Assert.Empty(ids);
            using var read = _h.NewDb();
            Assert.Empty(read.Ideas.Where(i => i.SegmentId == segmentId).ToList());
        }

        public void Dispose() => _h.Dispose();
    }
}
