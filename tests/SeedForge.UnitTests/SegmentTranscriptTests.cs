using Microsoft.Extensions.Logging.Abstractions;
using SeedForge.Data;
using SeedForge.Domain;
using SeedForge.Features.Segmentation;
using SeedForge.UnitTests.Fakes;

namespace SeedForge.UnitTests
{
    /// <summary>Proves segmentation maps verbatim anchors to non-overlapping spans and always yields ≥1 segment.</summary>
    public class SegmentTranscriptTests : IDisposable
    {
        private readonly SliceTestHarness _h = new();

        private SegmentTranscriptHandler Handler(FakeLlmClient fake, ApplicationDbContext db) =>
            new(db, fake, _h.Resolver, NullLogger<SegmentTranscriptHandler>.Instance);

        [Fact]
        public async Task Two_anchors_produce_two_non_overlapping_segments_with_correct_spans()
        {
            const string text = "Today we talk about warp drives and faster than light travel. " +
                                 "Later we discuss alien first contact and how humanity reacts.";
            var transcriptId = _h.SeedTranscript(text);
            var fake = new FakeLlmClient().SetStructured(new SegmentationResponse(new()
            {
                new SegmentBoundaryDto(0, "Warp drives", "Today we talk about warp drives"),
                new SegmentBoundaryDto(1, "First contact", "Later we discuss alien first contact"),
            }));

            using var db = _h.NewDb();
            var ids = await Handler(fake, db).HandleAsync(new(transcriptId, "corr-seg"), default);

            Assert.Equal(2, ids.Count);
            using var read = _h.NewDb();
            var segments = read.Segments.Where(s => s.TranscriptId == transcriptId)
                .OrderBy(s => s.OrdinalIndex).ToList();
            Assert.Equal(2, segments.Count);
            Assert.Equal(0, segments[0].StartChar);
            Assert.Equal(segments[0].EndChar, segments[1].StartChar); // non-overlapping, contiguous
            Assert.Equal(text.Length, segments[1].EndChar);
            // Each segment's stored text is a faithful slice of the original.
            Assert.Equal(text.Substring(segments[0].StartChar, segments[0].EndChar - segments[0].StartChar), segments[0].Text);
            Assert.Equal(text.Substring(segments[1].StartChar, segments[1].EndChar - segments[1].StartChar), segments[1].Text);
            Assert.Equal(ModelSlot.Seed, Assert.Single(fake.Contexts).Slot);
            Assert.Equal("corr-seg", fake.Contexts[0].CorrelationId);
        }

        [Fact]
        public async Task Single_boundary_at_start_produces_one_whole_transcript_segment()
        {
            const string text = "A single uninterrupted monologue about one idea from start to finish.";
            var transcriptId = _h.SeedTranscript(text);
            var fake = new FakeLlmClient().SetStructured(new SegmentationResponse(new()
            {
                new SegmentBoundaryDto(0, "The idea", "A single uninterrupted monologue"),
            }));

            using var db = _h.NewDb();
            var ids = await Handler(fake, db).HandleAsync(new(transcriptId, "corr-1"), default);

            Assert.Single(ids);
            using var read = _h.NewDb();
            var segment = Assert.Single(read.Segments.Where(s => s.TranscriptId == transcriptId).ToList());
            Assert.Equal(0, segment.StartChar);
            Assert.Equal(text.Length, segment.EndChar);
            Assert.Equal(text, segment.Text);
        }

        [Fact]
        public async Task Empty_boundary_list_falls_back_to_one_whole_transcript_segment()
        {
            const string text = "Some transcript the model failed to segment.";
            var transcriptId = _h.SeedTranscript(text);
            var fake = new FakeLlmClient().SetStructured(new SegmentationResponse(new()));

            using var db = _h.NewDb();
            var ids = await Handler(fake, db).HandleAsync(new(transcriptId, "corr-empty"), default);

            Assert.Single(ids);
            using var read = _h.NewDb();
            var segment = Assert.Single(read.Segments.Where(s => s.TranscriptId == transcriptId).ToList());
            Assert.Equal(0, segment.StartChar);
            Assert.Equal(text.Length, segment.EndChar);
            Assert.Equal(text, segment.Text);
        }

        public void Dispose() => _h.Dispose();
    }
}
