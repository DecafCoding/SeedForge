using Microsoft.Extensions.Options;
using SeedForge.Domain;
using SeedForge.Features.Concepts;
using SeedForge.Services.Ai;
using SeedForge.Services.Queues;

namespace SeedForge.Workers
{
    /// <summary>
    /// One iteration of the Concept worker, extracted for unit testing: claim the next ConceptJob, build exactly one
    /// active <see cref="Concept"/> for its idea (honoring the job's optional profile/slot override), and mark the job
    /// Done. A thrown error reschedules with backoff. Auto and Regen jobs build identically — the trigger only records
    /// provenance. Scoped — uses the scoped services opened per tick by <see cref="ConceptWorker"/>.
    /// </summary>
    public sealed class ConceptIteration(
        ConceptQueue queue, BuildConceptHandler build, LlmOptionsResolver slots, ILogger<ConceptIteration> log)
    {
        /// <summary>Builds at most one concept. No-op (returns false) when the queue is empty.</summary>
        public async Task<bool> ProcessOnceAsync(CancellationToken ct)
        {
            var job = await queue.ClaimNextAsync(ct);
            if (job is null) return false;

            try
            {
                var correlationId = Guid.NewGuid().ToString("N");

                // Honor the job's optional override: a profile id (stored as text) and/or a non-default slot.
                int? profileId = int.TryParse(job.ProfileOverride, out var pid) ? pid : null;
                var slot = job.SlotOverride ?? ModelSlot.Concept;
                var options = slots.Resolve(slot, profileId);

                await build.HandleAsync(new BuildConceptRequest(job.IdeaId, correlationId, options), ct);
                await queue.CompleteAsync(job.Id, ct);
                log.LogInformation("Built concept for job {JobId} (idea {IdeaId}, trigger {Trigger})",
                    job.Id, job.IdeaId, job.Trigger);
            }
            catch (Exception ex)
            {
                await queue.FailAsync(job.Id, ct);
                log.LogError(ex, "Concept build for job {JobId} failed; rescheduled with backoff", job.Id);
            }
            return true;
        }
    }

    /// <summary>
    /// Background service draining the concept queue on its own cadence — isolated from processing so a slow/paid
    /// Concept model only backs up this queue, never the video queue. Singleton; opens a DI scope per tick, respects
    /// the pause flag, and sleeps until its interval elapses or a wake signal arrives. One job per tick.
    /// </summary>
    public sealed class ConceptWorker(
        IServiceScopeFactory scopeFactory,
        WorkerControl control,
        IOptions<WorkerOptions> options,
        ILogger<ConceptWorker> log) : BackgroundService
    {
        private readonly WorkerOptions _opts = options.Value;

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            log.LogInformation("ConceptWorker started (interval {Interval}s, starts paused)",
                _opts.ConceptIntervalSeconds);

            while (!stoppingToken.IsCancellationRequested)
            {
                if (!control.IsPaused(Worker.Concept))
                {
                    try
                    {
                        using var scope = scopeFactory.CreateScope();
                        var iteration = scope.ServiceProvider.GetRequiredService<ConceptIteration>();
                        await iteration.ProcessOnceAsync(stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        log.LogError(ex, "ConceptWorker iteration failed unexpectedly");
                    }
                }

                await control.WaitForNextAsync(
                    Worker.Concept, TimeSpan.FromSeconds(_opts.ConceptIntervalSeconds), stoppingToken);
            }

            log.LogInformation("ConceptWorker stopping");
        }
    }
}
