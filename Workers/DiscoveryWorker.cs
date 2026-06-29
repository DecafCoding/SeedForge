using Microsoft.Extensions.Options;
using SeedForge.Features.Discovery;

namespace SeedForge.Workers
{
    /// <summary>
    /// One iteration of the Discovery worker, extracted so it is unit-testable without the timed host loop: poll the
    /// whole channel library once (<see cref="PollChannelsHandler"/> with no channel filter), enqueuing genuinely new
    /// uploads to the video queue. A per-channel failure is already caught inside the handler, so one bad channel never
    /// starves the rest. No transcript or AI work. Scoped — it uses the scoped <see cref="PollChannelsHandler"/> opened
    /// per tick by <see cref="DiscoveryWorker"/>.
    /// </summary>
    public sealed class DiscoveryIteration(PollChannelsHandler poll, ILogger<DiscoveryIteration> log)
    {
        /// <summary>Polls every channel in the library once, returning the total number of new uploads enqueued.</summary>
        public async Task<int> ProcessOnceAsync(CancellationToken ct)
        {
            var result = await poll.HandleAsync(new PollChannelsRequest(ChannelId: null), ct);
            var enqueued = result.Channels.Sum(c => c.NewVideoCount);
            log.LogInformation("Discovery polled {Channels} channel(s); enqueued {New} new upload(s)",
                result.Channels.Count, enqueued);
            return enqueued;
        }
    }

    /// <summary>
    /// Background service polling the channel library on a daily cadence. As a singleton it opens a DI scope per tick
    /// (<see cref="IServiceScopeFactory"/>) to reach the scoped <see cref="DiscoveryIteration"/>, respects the pause
    /// flag, and sleeps until its interval elapses or a wake signal arrives. Starts paused (like the other workers).
    /// </summary>
    public sealed class DiscoveryWorker(
        IServiceScopeFactory scopeFactory,
        WorkerControl control,
        IOptions<WorkerOptions> options,
        ILogger<DiscoveryWorker> log) : BackgroundService
    {
        private readonly WorkerOptions _opts = options.Value;

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            log.LogInformation("DiscoveryWorker started (interval {Interval}s, starts paused)",
                _opts.DiscoveryIntervalSeconds);

            while (!stoppingToken.IsCancellationRequested)
            {
                if (!control.IsPaused(Worker.Discovery))
                {
                    try
                    {
                        using var scope = scopeFactory.CreateScope();
                        var iteration = scope.ServiceProvider.GetRequiredService<DiscoveryIteration>();
                        await iteration.ProcessOnceAsync(stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        // Belt-and-braces: per-channel errors are swallowed inside the handler; never let the loop die.
                        log.LogError(ex, "DiscoveryWorker iteration failed unexpectedly");
                    }
                }

                await control.WaitForNextAsync(
                    Worker.Discovery, TimeSpan.FromSeconds(_opts.DiscoveryIntervalSeconds), stoppingToken);
            }

            log.LogInformation("DiscoveryWorker stopping");
        }
    }
}
