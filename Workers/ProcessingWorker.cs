using Microsoft.Extensions.Options;
using SeedForge.Domain;
using SeedForge.Pipeline;
using SeedForge.Services.Queues;

namespace SeedForge.Workers
{
    /// <summary>
    /// What one processing tick did, so the worker loop can fast-forward past a "dud" (a video that yielded no ideas)
    /// while keeping the configured spacing between productive runs.
    /// </summary>
    public enum ProcessTickOutcome
    {
        /// <summary>The queue was empty — nothing was claimed.</summary>
        Idle,
        /// <summary>A video processed and produced at least one survivor (ConceptJob(s) enqueued).</summary>
        Productive,
        /// <summary>A video processed but yielded no ideas (<c>ProcessedNoIdeas</c> / <c>NoTranscript</c>) — a dud.</summary>
        NoIdeas,
        /// <summary>A video failed and was rescheduled with backoff (or capped to terminal <c>Failed</c>).</summary>
        Failed,
    }

    /// <summary>
    /// One iteration of the Processing worker, extracted so it is unit-testable without the timed host loop: claim the
    /// next video, process it to the scoring seam (<see cref="PipelineRunner.ProcessVideoJobAsync"/>), and report the
    /// outcome. A thrown error reschedules the job with backoff (never crashes the worker). Scoped — it uses the
    /// scoped <see cref="VideoQueue"/> / <see cref="PipelineRunner"/> opened per tick by <see cref="ProcessingWorker"/>.
    /// </summary>
    public sealed class ProcessingIteration(
        VideoQueue queue, PipelineRunner pipeline, ILogger<ProcessingIteration> log)
    {
        /// <summary>
        /// Processes at most one queued video and reports what happened. Returns <see cref="ProcessTickOutcome.Idle"/>
        /// when the queue is empty so the worker can sleep; <see cref="ProcessTickOutcome.NoIdeas"/> on a dud so the
        /// worker can jump straight to the next video.
        /// </summary>
        public async Task<ProcessTickOutcome> ProcessOnceAsync(CancellationToken ct)
        {
            var video = await queue.ClaimNextAsync(ct);
            if (video is null) return ProcessTickOutcome.Idle;

            try
            {
                var result = await pipeline.ProcessVideoJobAsync(video.Id, ct);
                await queue.CompleteAsync(video.Id, result.Status, ct);
                log.LogInformation("Processed video {VideoId} ⇒ {Status}", video.Id, result.Status);
                return result.Status switch
                {
                    VideoJobStatus.Done => ProcessTickOutcome.Productive,
                    VideoJobStatus.ProcessedNoIdeas or VideoJobStatus.NoTranscript => ProcessTickOutcome.NoIdeas,
                    _ => ProcessTickOutcome.Failed,
                };
            }
            catch (Exception ex)
            {
                // Transient failure (e.g. a model error during scoring): reschedule with backoff, keep the loop alive.
                await queue.FailAsync(video.Id, ex.Message, ct);
                log.LogError(ex, "Processing video {VideoId} failed; rescheduled with backoff", video.Id);
                return ProcessTickOutcome.Failed;
            }
        }
    }

    /// <summary>
    /// Background service draining the video queue on its own cadence. As a singleton it opens a DI scope per tick
    /// (<see cref="IServiceScopeFactory"/>) to reach the scoped <see cref="ProcessingIteration"/>, respects the pause
    /// flag, and sleeps until its interval elapses or a wake signal arrives. One item per tick (PRD).
    /// </summary>
    public sealed class ProcessingWorker(
        IServiceScopeFactory scopeFactory,
        WorkerControl control,
        IOptions<WorkerOptions> options,
        ILogger<ProcessingWorker> log) : BackgroundService
    {
        private readonly WorkerOptions _opts = options.Value;

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            log.LogInformation("ProcessingWorker started (interval {Interval}s, starts paused)",
                _opts.ProcessingIntervalSeconds);

            while (!stoppingToken.IsCancellationRequested)
            {
                var outcome = ProcessTickOutcome.Idle;
                if (!control.IsPaused(Worker.Processing))
                {
                    try
                    {
                        using var scope = scopeFactory.CreateScope();
                        var iteration = scope.ServiceProvider.GetRequiredService<ProcessingIteration>();
                        outcome = await iteration.ProcessOnceAsync(stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        // Belt-and-braces: ProcessOnceAsync already swallows job errors; never let the loop die.
                        log.LogError(ex, "ProcessingWorker iteration failed unexpectedly");
                    }
                }

                // Fast-forward past a dud: when a video yielded no ideas, jump straight to the next so it never holds
                // the queue. Productive runs keep the configured spacing, and an empty queue / failed job / pause falls
                // through to the interval sleep (a Signal still wakes it early).
                if (outcome == ProcessTickOutcome.NoIdeas && !stoppingToken.IsCancellationRequested)
                {
                    continue;
                }

                await control.WaitForNextAsync(
                    Worker.Processing, TimeSpan.FromSeconds(_opts.ProcessingIntervalSeconds), stoppingToken);
            }

            log.LogInformation("ProcessingWorker stopping");
        }
    }
}
