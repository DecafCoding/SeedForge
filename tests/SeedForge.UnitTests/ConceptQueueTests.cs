using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SeedForge.Domain;
using SeedForge.Services.Queues;
using SeedForge.UnitTests.Fakes;
using SeedForge.Workers;

namespace SeedForge.UnitTests
{
    /// <summary>The durable concept queue: enqueue auto/regen, atomic claim, and exponential backoff to terminal Failed.</summary>
    public class ConceptQueueTests : IDisposable
    {
        private readonly SliceTestHarness _h = new();
        private readonly WorkerOptions _opts = new() { MaxAttempts = 3, BackoffBaseSeconds = 30 };

        private ConceptQueue NewQueue() =>
            new(_h.NewDb(), Options.Create(_opts), new WorkerControl(), NullLogger<ConceptQueue>.Instance);

        /// <summary>Seeds an idea (transcript → segment → idea) so a ConceptJob's FK is satisfiable.</summary>
        private int SeedIdea()
        {
            var transcriptId = _h.SeedTranscript("a transcript");
            var segmentId = _h.SeedSegment(transcriptId, "a segment");
            return _h.SeedIdea(segmentId, "a premise", "corr");
        }

        [Fact]
        public async Task Enqueue_auto_and_regen_with_override_are_both_claimable()
        {
            var ideaId = SeedIdea();

            var autoId = await NewQueue().EnqueueAsync(ideaId, ConceptTrigger.Auto);
            var regenId = await NewQueue().EnqueueAsync(ideaId, ConceptTrigger.Regen, profileId: 7, slotOverride: ModelSlot.Concept);

            using (var read = _h.NewDb())
            {
                var regen = read.ConceptJobs.Single(j => j.Id == regenId);
                Assert.Equal(ConceptTrigger.Regen, regen.Trigger);
                Assert.Equal("7", regen.ProfileOverride);
                Assert.Equal(ModelSlot.Concept, regen.SlotOverride);
            }

            // Both Pending: the oldest (auto) claims first, then the regen job.
            var firstClaim = await NewQueue().ClaimNextAsync();
            Assert.Equal(autoId, firstClaim!.Id);
            Assert.Equal(ConceptJobStatus.InProgress, firstClaim.Status);

            var secondClaim = await NewQueue().ClaimNextAsync();
            Assert.Equal(regenId, secondClaim!.Id);
        }

        [Fact]
        public async Task Fail_reschedules_with_backoff_then_caps_to_terminal_failed()
        {
            var ideaId = SeedIdea();
            var jobId = await NewQueue().EnqueueAsync(ideaId, ConceptTrigger.Auto);

            await NewQueue().FailAsync(jobId);
            using (var read = _h.NewDb())
            {
                var j = read.ConceptJobs.Single(x => x.Id == jobId);
                Assert.Equal(ConceptJobStatus.Pending, j.Status);
                Assert.Equal(1, j.AttemptCount);
                Assert.True(j.NextAttemptUtc > DateTime.UtcNow);
            }

            await NewQueue().FailAsync(jobId);
            await NewQueue().FailAsync(jobId);
            using var final = _h.NewDb();
            var fj = final.ConceptJobs.Single(x => x.Id == jobId);
            Assert.Equal(ConceptJobStatus.Failed, fj.Status);
            Assert.Equal(3, fj.AttemptCount);
            Assert.Null(fj.NextAttemptUtc);
        }

        [Fact]
        public async Task Complete_marks_the_job_done()
        {
            var ideaId = SeedIdea();
            var jobId = await NewQueue().EnqueueAsync(ideaId, ConceptTrigger.Auto);
            await NewQueue().ClaimNextAsync();

            await NewQueue().CompleteAsync(jobId);

            using var read = _h.NewDb();
            Assert.Equal(ConceptJobStatus.Done, read.ConceptJobs.Single(j => j.Id == jobId).Status);
        }

        [Fact]
        public async Task ProcessNow_makes_the_targeted_job_claim_first()
        {
            var ideaId = SeedIdea();
            var firstJob = await NewQueue().EnqueueAsync(ideaId, ConceptTrigger.Auto);
            var secondJob = await NewQueue().EnqueueAsync(ideaId, ConceptTrigger.Auto);

            await NewQueue().ProcessNowAsync(secondJob);

            var claimed = await NewQueue().ClaimNextAsync();
            Assert.Equal(secondJob, claimed!.Id);
        }

        public void Dispose() => _h.Dispose();
    }
}
