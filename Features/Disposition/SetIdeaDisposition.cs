using Microsoft.EntityFrameworkCore;
using SeedForge.Data;
using SeedForge.Domain;
using SeedForge.Services.Queues;

namespace SeedForge.Features.Disposition
{
    /// <param name="IdeaId">The idea whose manual verdict is being set.</param>
    /// <param name="Disposition">The user's verdict (Keep enqueues a concept build; Skip cancels pending ones).</param>
    public sealed record SetIdeaDispositionRequest(int IdeaId, IdeaDisposition Disposition);

    /// <summary>
    /// Records the user's manual Keep/Skip verdict on an idea — the sole gate to concept creation. Keep enqueues an
    /// Auto <see cref="ConceptJob"/> unless the idea already has a concept or an open job (idempotent), and works
    /// regardless of the automated score (a failed idea can be rescued). Skip deletes any still-Pending jobs to avoid
    /// wasted LLM spend but never touches InProgress/Done jobs or existing concepts (stale-not-deleted doctrine).
    /// </summary>
    public sealed class SetIdeaDispositionHandler(
        ApplicationDbContext db,
        ConceptQueue conceptQueue,
        ILogger<SetIdeaDispositionHandler> log)
    {
        public async Task HandleAsync(SetIdeaDispositionRequest req, CancellationToken ct)
        {
            var idea = await db.Ideas.FirstOrDefaultAsync(i => i.Id == req.IdeaId, ct)
                ?? throw new InvalidOperationException($"Idea {req.IdeaId} not found");

            if (idea.Disposition == req.Disposition)
            {
                return; // double-click safe: same verdict is a no-op (no re-enqueue, no timestamp bump)
            }

            idea.Disposition = req.Disposition;
            idea.DispositionAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            log.LogInformation("Idea {IdeaId} disposition set to {Disposition}", req.IdeaId, req.Disposition);

            if (req.Disposition == IdeaDisposition.Keep)
            {
                // Idempotent enqueue: skip when a concept already exists or a job is already open.
                var hasConcept = await db.Concepts.AnyAsync(c => c.IdeaId == req.IdeaId, ct);
                var hasOpenJob = await db.ConceptJobs.AnyAsync(
                    j => j.IdeaId == req.IdeaId
                         && (j.Status == ConceptJobStatus.Pending || j.Status == ConceptJobStatus.InProgress), ct);
                if (!hasConcept && !hasOpenJob)
                {
                    await conceptQueue.EnqueueAsync(req.IdeaId, ConceptTrigger.Auto, ct: ct);
                }
            }
            else if (req.Disposition == IdeaDisposition.Skip)
            {
                // Cancel work not yet started; anything InProgress/Done stands (queue rows aren't lineage,
                // but a started build is left to finish rather than yanked mid-flight).
                var pending = await db.ConceptJobs
                    .Where(j => j.IdeaId == req.IdeaId && j.Status == ConceptJobStatus.Pending)
                    .ToListAsync(ct);
                if (pending.Count > 0)
                {
                    db.ConceptJobs.RemoveRange(pending);
                    await db.SaveChangesAsync(ct);
                    log.LogInformation("Idea {IdeaId} skipped: removed {Count} pending concept job(s)",
                        req.IdeaId, pending.Count);
                }
            }
        }
    }
}
