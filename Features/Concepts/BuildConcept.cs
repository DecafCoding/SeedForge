using Microsoft.EntityFrameworkCore;
using SeedForge.Data;
using SeedForge.Domain;
using SeedForge.Services.Ai;

namespace SeedForge.Features.Concepts
{
    /// <param name="OptionsOverride">
    /// When non-null, the Concept slot's resolved options are bypassed in favor of these — the single code path the
    /// auto pipeline (null ⇒ resolve Concept slot) and run-now regeneration (override) both flow through.
    /// </param>
    public sealed record BuildConceptRequest(int IdeaId, string CorrelationId, LlmOptions? OptionsOverride = null);

    /// <summary>The structured concept: the six developed fields for a story.</summary>
    public sealed record BuildConceptResponse(
        string Title, string Genre, string Theme, string Hook, string Conflict, string CentralStoryQuestion);

    /// <summary>
    /// Concept-builder stage (slot Concept): develops one idea into an append-only <see cref="Concept"/>, marking
    /// it active and flipping any prior active concept for that idea off in the same save.
    /// </summary>
    public sealed class BuildConceptHandler(
        ApplicationDbContext db, ILlmClient llm, LlmOptionsResolver slots,
        ILogger<BuildConceptHandler> log)
    {
        public const string PromptVersion = "v1";

        private const string System =
            "You develop a science-fiction story premise into a full concept. Return: " +
            "Title; Genre; Theme; Hook (a one-sentence pitch); Conflict (the core dramatic tension); and " +
            "CentralStoryQuestion phrased as \"Will X achieve Y against Z?\". Keep each field tight and concrete.";

        public async Task<int> HandleAsync(BuildConceptRequest req, CancellationToken ct)
        {
            var idea = await db.Ideas.FindAsync([req.IdeaId], ct)
                ?? throw new InvalidOperationException($"Idea {req.IdeaId} not found");

            // Latest score (if any) for the lineage link; the handler develops whatever idea it's given.
            var latestScoreId = await db.IdeaScores
                .Where(s => s.IdeaId == idea.Id)
                .OrderByDescending(s => s.Id)
                .Select(s => (int?)s.Id)
                .FirstOrDefaultAsync(ct);

            var resolved = req.OptionsOverride ?? slots.Resolve(ModelSlot.Concept);
            var ctx = new AiCallContext(req.CorrelationId, "ConceptBuilder", ModelSlot.Concept,
                                        nameof(Idea), idea.Id);

            var result = await llm.CompleteStructuredAsync<BuildConceptResponse>(
                resolved,
                [new ChatMessage("system", System), new ChatMessage("user", idea.Premise)],
                ctx, ct);

            // Append-only with one active: flip any prior active concept for this idea off first.
            var priorActive = await db.Concepts
                .Where(c => c.IdeaId == idea.Id && c.IsActive)
                .ToListAsync(ct);
            foreach (var prior in priorActive) prior.IsActive = false;

            var concept = new Concept
            {
                IdeaId = idea.Id,
                Title = result.Title,
                Genre = result.Genre,
                Theme = result.Theme,
                Hook = result.Hook,
                Conflict = result.Conflict,
                CentralStoryQuestion = result.CentralStoryQuestion,
                Slot = ModelSlot.Concept,
                Model = resolved.Model,
                PromptVersion = PromptVersion,
                IdeaScoreId = latestScoreId,
                IsActive = true,
                CreatedAtUtc = DateTime.UtcNow,
            };
            db.Concepts.Add(concept);
            await db.SaveChangesAsync(ct);

            log.LogInformation("Built concept {ConceptId} for idea {IdeaId} (corr {Corr})",
                concept.Id, idea.Id, req.CorrelationId);

            return concept.Id;
        }
    }
}
