using SeedForge.Data;
using SeedForge.Domain;
using SeedForge.Services.Ai;

namespace SeedForge.Features.Extraction
{
    public sealed record ExtractIdeasRequest(int SegmentId, string CorrelationId);

    /// <summary>One thin, self-contained premise extracted from a segment.</summary>
    public sealed record ThinIdeaDto(string Premise);

    /// <summary>The structured extraction result: 0..N thin ideas.</summary>
    public sealed record ExtractIdeasResponse(List<ThinIdeaDto> Ideas);

    /// <summary>Extractor stage (slot Extraction): segment text → 0..N immutable <see cref="Idea"/> rows (premise only).</summary>
    public sealed class ExtractIdeasHandler(
        ApplicationDbContext db, ILlmClient llm, LlmOptionsResolver slots,
        ILogger<ExtractIdeasHandler> log)
    {
        public const string PromptVersion = "v1";

        private const string System =
            "You read a passage from a video transcript and extract distinct science-fiction story premises. " +
            "Be greedy: surface every separable premise, even small ones. Each premise must be a single, " +
            "self-contained sentence that stands on its own without the surrounding passage. " +
            "If the passage contains no usable premise, return an empty list.";

        public async Task<IReadOnlyList<int>> HandleAsync(ExtractIdeasRequest req, CancellationToken ct)
        {
            var segment = await db.Segments.FindAsync([req.SegmentId], ct)
                ?? throw new InvalidOperationException($"Segment {req.SegmentId} not found");

            var options = slots.Resolve(ModelSlot.Extraction);
            var ctx = new AiCallContext(req.CorrelationId, "Extractor", ModelSlot.Extraction,
                                        nameof(Segment), segment.Id);

            var result = await llm.CompleteStructuredAsync<ExtractIdeasResponse>(
                options,
                [new ChatMessage("system", System), new ChatMessage("user", segment.Text)],
                ctx, ct);

            var now = DateTime.UtcNow;
            var ideas = (result.Ideas ?? new())
                .Where(i => !string.IsNullOrWhiteSpace(i.Premise))
                .Select(i => new Idea
                {
                    SegmentId = segment.Id,
                    Premise = i.Premise.Trim(),
                    CorrelationId = req.CorrelationId,
                    ExtractionPromptVersion = PromptVersion,
                    CreatedAtUtc = now,
                })
                .ToList();

            if (ideas.Count == 0)
            {
                log.LogInformation("Extracted 0 ideas from segment {SegmentId} (corr {Corr})",
                    segment.Id, req.CorrelationId);
                return Array.Empty<int>();
            }

            db.Ideas.AddRange(ideas);
            await db.SaveChangesAsync(ct);

            log.LogInformation("Extracted {Count} idea(s) from segment {SegmentId} (corr {Corr})",
                ideas.Count, segment.Id, req.CorrelationId);

            return ideas.Select(i => i.Id).ToList();
        }
    }
}
