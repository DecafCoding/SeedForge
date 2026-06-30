using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SeedForge.Domain;
using SeedForge.Services.Queues;
using SeedForge.UnitTests.Fakes;
using SeedForge.Workers;

namespace SeedForge.UnitTests
{
    /// <summary>The durable video queue: atomic claim, exponential backoff to terminal Failed, and process-now priority.</summary>
    public class VideoQueueTests : IDisposable
    {
        private readonly SliceTestHarness _h = new();
        private readonly WorkerOptions _opts = new() { MaxAttempts = 3, BackoffBaseSeconds = 30 };

        private VideoQueue NewQueue() =>
            new(_h.NewDb(), Options.Create(_opts), new WorkerControl(), NullLogger<VideoQueue>.Instance);

        [Fact]
        public async Task Enqueue_then_claim_flips_pending_to_in_progress()
        {
            var id = await NewQueue().EnqueueAsync("abc12345678");

            using (var read = _h.NewDb())
            {
                Assert.Equal(VideoJobStatus.Pending, read.Videos.Single(v => v.Id == id).Status);
            }

            var claimed = await NewQueue().ClaimNextAsync();

            Assert.NotNull(claimed);
            Assert.Equal(id, claimed!.Id);
            using var after = _h.NewDb();
            Assert.Equal(VideoJobStatus.InProgress, after.Videos.Single(v => v.Id == id).Status);
        }

        [Fact]
        public async Task Fail_reschedules_with_backoff_then_caps_to_terminal_failed()
        {
            var id = await NewQueue().EnqueueAsync("abc12345678");

            // First failure: rescheduled Pending with a future NextAttemptUtc.
            await NewQueue().FailAsync(id, "boom");
            using (var read = _h.NewDb())
            {
                var v = read.Videos.Single(x => x.Id == id);
                Assert.Equal(VideoJobStatus.Pending, v.Status);
                Assert.Equal(1, v.AttemptCount);
                Assert.NotNull(v.NextAttemptUtc);
                Assert.True(v.NextAttemptUtc > DateTime.UtcNow);
            }

            // Failures 2 and 3 reach MaxAttempts ⇒ terminal Failed, no further retry scheduled.
            await NewQueue().FailAsync(id);
            await NewQueue().FailAsync(id);
            using var final = _h.NewDb();
            var fv = final.Videos.Single(x => x.Id == id);
            Assert.Equal(VideoJobStatus.Failed, fv.Status);
            Assert.Equal(3, fv.AttemptCount);
            Assert.Null(fv.NextAttemptUtc);
        }

        [Fact]
        public async Task Claim_skips_a_row_whose_backoff_is_in_the_future()
        {
            var id = await NewQueue().EnqueueAsync("abc12345678");
            await NewQueue().FailAsync(id); // now Pending but NextAttemptUtc in the future

            var claimed = await NewQueue().ClaimNextAsync();

            Assert.Null(claimed); // not yet due
        }

        [Fact]
        public async Task ProcessNow_makes_the_targeted_row_claim_first()
        {
            var first = await NewQueue().EnqueueAsync("aaa11111111");
            var second = await NewQueue().EnqueueAsync("bbb22222222");

            // By default the oldest (first) would be claimed; process-now on the second jumps it ahead.
            await NewQueue().ProcessNowAsync(second);

            var claimed = await NewQueue().ClaimNextAsync();
            Assert.NotNull(claimed);
            Assert.Equal(second, claimed!.Id);
        }

        [Fact]
        public async Task Enqueue_persists_a_supplied_title_on_a_new_row()
        {
            var id = await NewQueue().EnqueueAsync("abc12345678", "Terraforming Mars");

            using var read = _h.NewDb();
            Assert.Equal("Terraforming Mars", read.Videos.Single(v => v.Id == id).Title);
        }

        [Fact]
        public async Task Enqueue_backfills_a_missing_title_but_never_clobbers_an_existing_one()
        {
            // First discovery had no title; a later re-arm supplies one ⇒ backfilled.
            var id = await NewQueue().EnqueueAsync("abc12345678");
            await NewQueue().EnqueueAsync("abc12345678", "Real Title");
            using (var read = _h.NewDb())
            {
                Assert.Equal("Real Title", read.Videos.Single(v => v.Id == id).Title);
            }

            // A subsequent re-arm with a different title must NOT overwrite the captured one.
            await NewQueue().EnqueueAsync("abc12345678", "Different Title");
            using var read2 = _h.NewDb();
            Assert.Equal("Real Title", read2.Videos.Single(v => v.Id == id).Title);
        }

        [Theory]
        [InlineData(VideoJobStatus.Done)]
        [InlineData(VideoJobStatus.ProcessedNoIdeas)]
        [InlineData(VideoJobStatus.NoTranscript)]
        public async Task Complete_with_terminal_outcome_stamps_processed_time(VideoJobStatus status)
        {
            var id = await NewQueue().EnqueueAsync("abc12345678");
            await NewQueue().ClaimNextAsync();

            await NewQueue().CompleteAsync(id, status);

            using var read = _h.NewDb();
            var v = read.Videos.Single(x => x.Id == id);
            Assert.Equal(status, v.Status);
            Assert.NotNull(v.ProcessedAtUtc);
        }

        [Fact]
        public async Task Complete_with_failed_status_does_not_stamp_processed_time()
        {
            var id = await NewQueue().EnqueueAsync("abc12345678");
            await NewQueue().ClaimNextAsync();

            await NewQueue().CompleteAsync(id, VideoJobStatus.Failed);

            using var read = _h.NewDb();
            Assert.Null(read.Videos.Single(x => x.Id == id).ProcessedAtUtc);
        }

        public void Dispose() => _h.Dispose();
    }
}
