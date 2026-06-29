using Microsoft.EntityFrameworkCore;
using SeedForge.Data;

namespace SeedForge.Features.Scoring
{
    /// <param name="IdeaId">The idea to re-score (its prior scores are kept; a new one is appended).</param>
    public sealed record RescoreIdeaRequest(int IdeaId, string? CorrelationId = null);

    /// <summary>
    /// Re-scores a single idea by reusing <see cref="ScoreIdeasHandler"/> (appending one new <c>IdeaScore</c>), then
    /// applies the stale-not-deleted cascade: an idea's concepts are flagged <c>IsStale = !PassedThreshold</c> on the
    /// new score and never deleted. Co-located with <see cref="ScoreIdeas"/> so reuse stays in-slice.
    /// </summary>
    public sealed class RescoreIdeaHandler(ApplicationDbContext db, ScoreIdeasHandler score)
    {
        public async Task HandleAsync(RescoreIdeaRequest req, CancellationToken ct)
        {
            var correlationId = req.CorrelationId ?? Guid.NewGuid().ToString("N");

            // Reuse the one-pass scorer for this single idea; it appends a fresh IdeaScore row.
            await score.HandleAsync(new ScoreIdeasRequest(new[] { req.IdeaId }, correlationId), ct);

            // The just-appended (latest) score drives the cascade.
            var latest = await db.IdeaScores
                .Where(s => s.IdeaId == req.IdeaId)
                .OrderByDescending(s => s.Id)
                .FirstOrDefaultAsync(ct);
            if (latest is null)
            {
                return; // scorer produced no row (e.g. idea not found) — nothing to cascade
            }

            // Stale-not-deleted: flag the idea's concepts; cleared on a pass, set on a drop below threshold. Never deleted.
            var concepts = await db.Concepts.Where(c => c.IdeaId == req.IdeaId).ToListAsync(ct);
            foreach (var c in concepts) c.IsStale = !latest.PassedThreshold;
            await db.SaveChangesAsync(ct);
        }
    }
}
