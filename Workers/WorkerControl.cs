using System.Collections.Concurrent;

namespace SeedForge.Workers
{
    /// <summary>The two background workers, used to key pause flags and wake signals.</summary>
    public enum Worker
    {
        Processing,
        Concept
    }

    /// <summary>
    /// Singleton coordination surface shared by the workers and the UI: a per-worker paused flag and a wake signal.
    /// <see cref="WaitForNextAsync"/> sleeps a worker until its interval elapses, a <see cref="Signal"/> arrives
    /// (enqueue / process-now), or shutdown is requested — so an operator action never waits the full interval.
    /// Workers <strong>default to paused on boot</strong> to avoid surprise Apify/model spend; the operator resumes them.
    /// </summary>
    public sealed class WorkerControl : IDisposable
    {
        private readonly ConcurrentDictionary<Worker, bool> _paused = new();
        private readonly Dictionary<Worker, SemaphoreSlim> _signals = new()
        {
            [Worker.Processing] = new SemaphoreSlim(0, 1),
            [Worker.Concept] = new SemaphoreSlim(0, 1),
        };

        public WorkerControl()
        {
            // Paused by default until the operator resumes from /queues.
            _paused[Worker.Processing] = true;
            _paused[Worker.Concept] = true;
        }

        public bool IsPaused(Worker worker) => _paused.TryGetValue(worker, out var p) && p;

        public void Pause(Worker worker) => _paused[worker] = true;

        public void Resume(Worker worker)
        {
            _paused[worker] = false;
            Signal(worker); // wake immediately so a resume drains a backlog without waiting the interval
        }

        /// <summary>Wakes a worker that is currently sleeping in <see cref="WaitForNextAsync"/>. Idempotent (coalesced).</summary>
        public void Signal(Worker worker)
        {
            var sem = _signals[worker];
            // Only release when no pending signal exists, so repeated calls don't accumulate into a busy loop.
            if (sem.CurrentCount == 0)
            {
                try { sem.Release(); }
                catch (SemaphoreFullException) { /* already signaled — fine */ }
            }
        }

        /// <summary>
        /// Sleeps until <paramref name="interval"/> elapses, a signal arrives, or <paramref name="ct"/> is cancelled —
        /// whichever comes first. Never throws on cancellation, so the worker loop can exit cleanly on shutdown.
        /// </summary>
        public async Task WaitForNextAsync(Worker worker, TimeSpan interval, CancellationToken ct)
        {
            try
            {
                await _signals[worker].WaitAsync(interval, ct);
            }
            catch (OperationCanceledException)
            {
                // Shutdown requested: return so ExecuteAsync's while-loop condition stops the worker.
            }
        }

        public void Dispose()
        {
            foreach (var sem in _signals.Values) sem.Dispose();
        }
    }
}
