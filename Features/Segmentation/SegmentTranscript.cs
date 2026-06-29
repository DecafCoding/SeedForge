using Microsoft.EntityFrameworkCore;
using SeedForge.Data;
using SeedForge.Domain;
using SeedForge.Services.Ai;

namespace SeedForge.Features.Segmentation
{
    public sealed record SegmentTranscriptRequest(int TranscriptId, string CorrelationId);

    /// <summary>One subject boundary the model identified, anchored by a verbatim quote from the transcript.</summary>
    public sealed record SegmentBoundaryDto(int OrdinalIndex, string Subject, string StartAnchor);

    /// <summary>The structured segmentation result: one boundary per distinct subject.</summary>
    public sealed record SegmentationResponse(List<SegmentBoundaryDto> Segments);

    /// <summary>Segmenter stage (slot Seed): a transcript → 1..N immutable subject-bounded <see cref="Segment"/> rows.</summary>
    public sealed class SegmentTranscriptHandler(
        ApplicationDbContext db, ILlmClient llm, LlmOptionsResolver slots,
        ILogger<SegmentTranscriptHandler> log)
    {
        public const string PromptVersion = "v1";

        private const string System =
            "You split a video transcript into segments, one per distinct subject the speaker covers. " +
            "Return a list of boundaries in the order they appear. For each boundary provide: an OrdinalIndex " +
            "(0-based, ascending), a short Subject label, and a StartAnchor — a verbatim quote of 5 to 12 words " +
            "copied EXACTLY from the transcript marking where that subject begins. " +
            "If the transcript covers a single topic, return exactly one boundary anchored at its opening words. " +
            "Never paraphrase the StartAnchor; it must be findable as an exact substring of the transcript.";

        public async Task<IReadOnlyList<int>> HandleAsync(SegmentTranscriptRequest req, CancellationToken ct)
        {
            var transcript = await db.Transcripts.FindAsync([req.TranscriptId], ct)
                ?? throw new InvalidOperationException($"Transcript {req.TranscriptId} not found");

            var text = transcript.PlainText ?? string.Empty;
            var options = slots.Resolve(ModelSlot.Seed);
            var ctx = new AiCallContext(req.CorrelationId, "Segmenter", ModelSlot.Seed,
                                        nameof(Transcript), transcript.Id);

            var result = await llm.CompleteStructuredAsync<SegmentationResponse>(
                options,
                [new ChatMessage("system", System), new ChatMessage("user", text)],
                ctx, ct);

            var starts = ResolveBoundaryStarts(text, result.Segments);
            var segments = BuildSegments(text, starts);

            var now = DateTime.UtcNow;
            for (var i = 0; i < segments.Count; i++)
            {
                segments[i].TranscriptId = transcript.Id;
                segments[i].OrdinalIndex = i;
                segments[i].CreatedAtUtc = now;
            }

            db.Segments.AddRange(segments);
            await db.SaveChangesAsync(ct);

            log.LogInformation("Segmented transcript {TranscriptId} into {Count} segment(s) (corr {Corr})",
                transcript.Id, segments.Count, req.CorrelationId);

            return segments.Select(s => s.Id).ToList();
        }

        /// <summary>
        /// Maps each boundary's verbatim StartAnchor to a character offset by locating it (ordinal,
        /// case-insensitive) at/after the previous boundary. Unfound anchors are skipped. Returns ascending,
        /// de-duplicated start offsets; empty when nothing resolved.
        /// </summary>
        private static List<int> ResolveBoundaryStarts(string text, List<SegmentBoundaryDto>? boundaries)
        {
            var starts = new List<int>();
            if (boundaries is null || text.Length == 0) return starts;

            var searchFrom = 0;
            foreach (var boundary in boundaries.OrderBy(b => b.OrdinalIndex))
            {
                var anchor = boundary.StartAnchor;
                if (string.IsNullOrWhiteSpace(anchor)) continue;

                var idx = text.IndexOf(anchor, searchFrom, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) continue; // skip anchors that aren't faithful substrings

                if (starts.Count == 0 || idx > starts[^1])
                {
                    starts.Add(idx);
                    searchFrom = idx + anchor.Length;
                }
            }

            return starts;
        }

        /// <summary>
        /// Builds non-overlapping segments as faithful slices of the original text between consecutive starts.
        /// Guarantees at least one segment: no resolved starts ⇒ a single whole-transcript segment. The first
        /// segment always begins at offset 0 so coverage is complete.
        /// </summary>
        private static List<Segment> BuildSegments(string text, List<int> starts)
        {
            var segments = new List<Segment>();

            if (starts.Count == 0)
            {
                segments.Add(new Segment { StartChar = 0, EndChar = text.Length, Text = text });
                return segments;
            }

            // Ensure full coverage from the very start of the transcript.
            if (starts[0] != 0) starts.Insert(0, 0);

            for (var i = 0; i < starts.Count; i++)
            {
                var start = starts[i];
                var end = i + 1 < starts.Count ? starts[i + 1] : text.Length;
                segments.Add(new Segment
                {
                    StartChar = start,
                    EndChar = end,
                    Text = text.Substring(start, end - start),
                });
            }

            return segments;
        }
    }
}
