using SeedForge.Domain;
using SeedForge.Services.Ai;

namespace SeedForge.Features.Concepts
{
    /// <param name="IdeaId">The existing, immutable idea to rebuild a concept for.</param>
    /// <param name="ProfileId">Optional config profile to resolve the Concept slot from; null ⇒ active profile.</param>
    public sealed record RegenerateConceptRequest(int IdeaId, int? ProfileId = null, string? CorrelationId = null);

    /// <summary>
    /// Run-now (synchronous) regeneration of a single concept for an existing idea, with an optional profile
    /// override. Reuses <see cref="BuildConceptHandler"/> — the idea is immutable, so no upstream stage re-runs;
    /// this is exactly one Concept Builder call. Co-located with <see cref="BuildConcept"/> so reuse is in-slice.
    /// </summary>
    public sealed class RegenerateConceptHandler(BuildConceptHandler build, LlmOptionsResolver slots)
    {
        public async Task<int> HandleAsync(RegenerateConceptRequest req, CancellationToken ct)
        {
            var correlationId = req.CorrelationId ?? Guid.NewGuid().ToString("N");

            // Resolve the Concept slot from the chosen profile and hand it to BuildConcept as an override so auto
            // and manual regen share one path (append a new active concept, flip the prior active off).
            var options = slots.Resolve(ModelSlot.Concept, req.ProfileId);
            return await build.HandleAsync(new BuildConceptRequest(req.IdeaId, correlationId, options), ct);
        }
    }
}
