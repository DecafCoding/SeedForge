using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SeedForge.Data;
using SeedForge.Domain;
using SeedForge.Workers;

namespace SeedForge.Services.Queues
{
    /// <summary>
    /// Durable concept-job queue: a thin scoped service over <see cref="ConceptJob"/> rows, mirroring
    /// <see cref="VideoQueue"/>'s claim/backoff shape. Both auto (<see cref="ConceptTrigger.Auto"/>) and bulk-regen
    /// (<see cref="ConceptTrigger.Regen"/>) jobs live here and drain identically; the optional profile/slot override
    /// rides on the row so the Concept worker can honor it.
    /// </summary>
    public sealed class ConceptQueue(
        ApplicationDbContext db,
        IOptions<WorkerOptions> options,
        WorkerControl control,
        ILogger<ConceptQueue> log)
    {
        private readonly WorkerOptions _opts = options.Value;

        /// <summary>
        /// Enqueues a Pending <see cref="ConceptJob"/> for an idea. The optional <paramref name="profileId"/> is stored
        /// as <see cref="ConceptJob.ProfileOverride"/> (the resolver takes an int profile id) and the optional
        /// <paramref name="slotOverride"/> as <see cref="ConceptJob.SlotOverride"/>. Returns the job id.
        /// </summary>
        public async Task<int> EnqueueAsync(
            int ideaId, ConceptTrigger trigger, int? profileId = null, ModelSlot? slotOverride = null,
            CancellationToken ct = default)
        {
            var job = new ConceptJob
            {
                IdeaId = ideaId,
                Status = ConceptJobStatus.Pending,
                Trigger = trigger,
                ProfileOverride = profileId?.ToString(),
                SlotOverride = slotOverride,
                CreatedAtUtc = DateTime.UtcNow,
            };
            db.ConceptJobs.Add(job);
            await db.SaveChangesAsync(ct);
            log.LogInformation("Enqueued concept job {JobId} for idea {IdeaId} (trigger {Trigger})",
                job.Id, ideaId, trigger);
            return job.Id;
        }

        /// <summary>
        /// Atomically claims the next eligible Pending job (highest priority, then oldest), flipping it InProgress in a
        /// short transaction. Returns null when nothing is due. Backoff/priority are respected.
        /// </summary>
        public async Task<ConceptJob?> ClaimNextAsync(CancellationToken ct = default)
        {
            var now = DateTime.UtcNow;
            using var tx = await db.Database.BeginTransactionAsync(ct);

            var job = await db.ConceptJobs
                .Where(j => j.Status == ConceptJobStatus.Pending
                         && (j.NextAttemptUtc == null || j.NextAttemptUtc <= now))
                .OrderByDescending(j => j.Priority).ThenBy(j => j.Id)
                .FirstOrDefaultAsync(ct);

            if (job is null)
            {
                await tx.CommitAsync(ct);
                return null;
            }

            job.Status = ConceptJobStatus.InProgress;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return job;
        }

        /// <summary>Marks a claimed job Done.</summary>
        public async Task CompleteAsync(int jobId, CancellationToken ct = default)
        {
            var job = await db.ConceptJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct)
                ?? throw new InvalidOperationException($"ConceptJob {jobId} not found");
            job.Status = ConceptJobStatus.Done;
            await db.SaveChangesAsync(ct);
        }

        /// <summary>
        /// Records a failed attempt: increments the attempt count and either reschedules with exponential backoff
        /// or, once <see cref="WorkerOptions.MaxAttempts"/> is reached, marks the job terminal <c>Failed</c>.
        /// </summary>
        public async Task FailAsync(int jobId, CancellationToken ct = default)
        {
            var job = await db.ConceptJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct)
                ?? throw new InvalidOperationException($"ConceptJob {jobId} not found");

            job.AttemptCount++;
            if (job.AttemptCount >= _opts.MaxAttempts)
            {
                job.Status = ConceptJobStatus.Failed;
                job.NextAttemptUtc = null;
            }
            else
            {
                job.Status = ConceptJobStatus.Pending;
                job.NextAttemptUtc = DateTime.UtcNow.AddSeconds(_opts.BackoffBaseSeconds * Math.Pow(2, job.AttemptCount));
            }
            await db.SaveChangesAsync(ct);
            log.LogWarning("ConceptJob {JobId} failed (attempt {Attempt}/{Max}); status {Status}",
                jobId, job.AttemptCount, _opts.MaxAttempts, job.Status);
        }

        /// <summary>Pending count for the queue view (drives the drain-time ETA).</summary>
        public Task<int> CountPendingAsync(CancellationToken ct = default) =>
            db.ConceptJobs.CountAsync(j => j.Status == ConceptJobStatus.Pending, ct);

        /// <summary>
        /// Jumps a job to the front of the queue: raises its priority, clears any backoff, and wakes the Concept worker
        /// via <see cref="WorkerControl"/> so it runs on the next tick rather than after the full interval.
        /// </summary>
        public async Task ProcessNowAsync(int jobId, CancellationToken ct = default)
        {
            var job = await db.ConceptJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct)
                ?? throw new InvalidOperationException($"ConceptJob {jobId} not found");
            job.Priority = Math.Max(job.Priority + 1, 1);
            job.NextAttemptUtc = DateTime.UtcNow;
            if (job.Status != ConceptJobStatus.InProgress)
            {
                job.Status = ConceptJobStatus.Pending;
            }
            await db.SaveChangesAsync(ct);
            control.Signal(Worker.Concept);
        }
    }
}
