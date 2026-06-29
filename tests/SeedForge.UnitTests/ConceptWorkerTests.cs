using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SeedForge.Data;
using SeedForge.Domain;
using SeedForge.Features.Concepts;
using SeedForge.Features.Config;
using SeedForge.Services.Queues;
using SeedForge.UnitTests.Fakes;
using SeedForge.Workers;

namespace SeedForge.UnitTests
{
    /// <summary>The Concept worker's extracted iteration: a claimed job builds one active concept (honoring overrides); a thrown error reschedules with backoff.</summary>
    public class ConceptWorkerTests : IDisposable
    {
        private readonly SliceTestHarness _h = new();
        private readonly WorkerOptions _opts = new() { MaxAttempts = 5, BackoffBaseSeconds = 30 };

        private static FakeLlmClient FakeWith(string title) => new FakeLlmClient().SetStructured(
            new BuildConceptResponse(title, "Sci-Fi", "Isolation", "A hook.", "A conflict.",
                "Will the captain save the crew against the AI?"));

        private (ConceptIteration Iteration, ConceptQueue Queue) Build(FakeLlmClient fake, ApplicationDbContext db)
        {
            var queue = new ConceptQueue(db, Options.Create(_opts), new WorkerControl(), NullLogger<ConceptQueue>.Instance);
            var build = new BuildConceptHandler(db, fake, _h.Resolver, NullLogger<BuildConceptHandler>.Instance);
            var iteration = new ConceptIteration(queue, build, _h.Resolver, NullLogger<ConceptIteration>.Instance);
            return (iteration, queue);
        }

        private int SeedIdea()
        {
            var transcriptId = _h.SeedTranscript("text");
            var segmentId = _h.SeedSegment(transcriptId, "seg");
            return _h.SeedIdea(segmentId, "a surviving premise", "corr-cw");
        }

        [Fact]
        public async Task ProcessOnceAsync_builds_one_active_concept_and_marks_the_job_done()
        {
            var ideaId = SeedIdea();
            var fake = FakeWith("Auto Concept");

            using var db = _h.NewDb();
            var (iteration, queue) = Build(fake, db);
            var jobId = await queue.EnqueueAsync(ideaId, ConceptTrigger.Auto);

            var processed = await iteration.ProcessOnceAsync(CancellationToken.None);

            Assert.True(processed);
            using var read = _h.NewDb();
            var concepts = read.Concepts.Where(c => c.IdeaId == ideaId).ToList();
            var active = Assert.Single(concepts);
            Assert.True(active.IsActive);
            Assert.Equal("Auto Concept", active.Title);
            Assert.Equal(ConceptJobStatus.Done, read.ConceptJobs.Single(j => j.Id == jobId).Status);
        }

        [Fact]
        public async Task ProcessOnceAsync_regen_with_profile_override_records_the_override_model()
        {
            var ideaId = SeedIdea();

            // Seed profiles so a non-active profile override resolves to a distinct (OpenAI) model.
            int profileId;
            using (var seedDb = _h.NewDb())
            {
                await ProfileSeeder.SeedAsync(seedDb, ResolverHarness.RigOptions());
                profileId = seedDb.ConfigProfiles.Single(p => p.Name == ProfileSeeder.AllOpenAi).Id;
            }

            var fake = FakeWith("Regen Concept");
            using var db = _h.NewDb();
            var (iteration, queue) = Build(fake, db);
            await queue.EnqueueAsync(ideaId, ConceptTrigger.Regen, profileId: profileId);

            await iteration.ProcessOnceAsync(CancellationToken.None);

            using var read = _h.NewDb();
            var active = Assert.Single(read.Concepts.Where(c => c.IdeaId == ideaId));
            Assert.Equal(ProfileSeeder.DefaultOpenAiModel, active.Model); // honored the override
        }

        [Fact]
        public async Task ProcessOnceAsync_reschedules_with_backoff_on_a_thrown_error_without_crashing()
        {
            var ideaId = SeedIdea();
            var fake = new FakeLlmClient(); // no BuildConcept response ⇒ the build throws

            using var db = _h.NewDb();
            var (iteration, queue) = Build(fake, db);
            var jobId = await queue.EnqueueAsync(ideaId, ConceptTrigger.Auto);

            var processed = await iteration.ProcessOnceAsync(CancellationToken.None);

            Assert.True(processed);
            using var read = _h.NewDb();
            var job = read.ConceptJobs.Single(j => j.Id == jobId);
            Assert.Equal(ConceptJobStatus.Pending, job.Status);
            Assert.Equal(1, job.AttemptCount);
            Assert.True(job.NextAttemptUtc > DateTime.UtcNow);
            Assert.Empty(read.Concepts);
        }

        public void Dispose() => _h.Dispose();
    }
}
