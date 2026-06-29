using System.Diagnostics;
using SeedForge.Workers;

namespace SeedForge.UnitTests
{
    /// <summary>The shared worker control: pause/resume gating and a wake signal that releases a sleeper early.</summary>
    public class WorkerControlTests
    {
        [Fact]
        public void Pause_and_Resume_toggle_IsPaused()
        {
            using var control = new WorkerControl();

            // Default-paused on boot (avoids surprise spend the moment the app starts).
            Assert.True(control.IsPaused(Worker.Processing));
            Assert.True(control.IsPaused(Worker.Concept));

            control.Resume(Worker.Processing);
            Assert.False(control.IsPaused(Worker.Processing));
            Assert.True(control.IsPaused(Worker.Concept)); // independent per worker

            control.Pause(Worker.Processing);
            Assert.True(control.IsPaused(Worker.Processing));
        }

        [Fact]
        public async Task WaitForNextAsync_returns_when_the_interval_elapses()
        {
            using var control = new WorkerControl();

            // No signal: returns after the (tiny) interval rather than hanging.
            await control.WaitForNextAsync(Worker.Processing, TimeSpan.FromMilliseconds(50), CancellationToken.None);
        }

        [Fact]
        public async Task Signal_releases_a_pending_wait_before_the_interval()
        {
            using var control = new WorkerControl();

            var sw = Stopwatch.StartNew();
            var waiting = control.WaitForNextAsync(Worker.Concept, TimeSpan.FromSeconds(30), CancellationToken.None);
            control.Signal(Worker.Concept);
            await waiting;
            sw.Stop();

            // Released by the signal, not by waiting out the 30s interval.
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"Wait took {sw.Elapsed}; signal did not release it early.");
        }

        [Fact]
        public async Task WaitForNextAsync_returns_promptly_on_cancellation()
        {
            using var control = new WorkerControl();
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            // Cancelled token: returns without throwing so the worker loop can exit cleanly.
            await control.WaitForNextAsync(Worker.Processing, TimeSpan.FromSeconds(30), cts.Token);
        }
    }
}
