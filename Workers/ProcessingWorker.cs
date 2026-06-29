using Microsoft.Extensions.Options;
using SeedForge.Domain;
using SeedForge.Pipeline;
using SeedForge.Services.Queues;

namespace SeedForge.Workers
{
    /// <summary>
    /// One iteration of the Processing worker, extracted so it is unit-testable without the timed host loop: claim the
    /// next video, process it to the scoring seam (<see cref="PipelineRunner.ProcessVideoJobAsync"/>), and record the
    /// outcome. A thrown error reschedules the job with backoff (never crashes the worker). Scoped — it uses the
    /// scoped <see cref="VideoQueue"/> / <see cref="PipelineRunner"/> opened per tick by <see cref="ProcessingWorker"/>.
    /// </summary>
    public sealed class ProcessingIteration(
        VideoQueue queue, PipelineRunner pipeline, ILogger<ProcessingIteration> log)
    {
        /// <summary>Processes at most one queued video. No-op (returns false) when the queue is empty.</summary>
        public async Task<bool> ProcessOnceAsync(CancellationToken ct)
        {
            var video = await queue.ClaimNextAsync(ct);
            if (video is null) return false;

            try
            {
                var result = await pipeline.ProcessVideoJobAsync(video.Id, ct);
                await queue.CompleteAsync(video.Id, result.Status, ct);
                log.LogInformation("Processed video {VideoId} ⇒ {Status}", video.Id, result.Status);
            }
            catch (Exception ex)
            {
                // Transient failure (e.g. a model error during scoring): reschedule with backoff, keep the loop alive.
                await queue.FailAsync(video.Id, ex.Message, ct);
                log.LogError(ex, "Processing video {VideoId} failed; rescheduled with backoff", video.Id);
            }
            return true;
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
                if (!control.IsPaused(Worker.Processing))
                {
                    try
                    {
                        using var scope = scopeFactory.CreateScope();
                        var iteration = scope.ServiceProvider.GetRequiredService<ProcessingIteration>();
                        await iteration.ProcessOnceAsync(stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        // Belt-and-braces: ProcessOnceAsync already swallows job errors; never let the loop die.
                        log.LogError(ex, "ProcessingWorker iteration failed unexpectedly");
                    }
                }

                await control.WaitForNextAsync(
                    Worker.Processing, TimeSpan.FromSeconds(_opts.ProcessingIntervalSeconds), stoppingToken);
            }

            log.LogInformation("ProcessingWorker stopping");
        }
    }
}
