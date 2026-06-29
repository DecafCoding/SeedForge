using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SeedForge.Data;
using SeedForge.Domain;
using SeedForge.Features;
using SeedForge.Features.Concepts;
using SeedForge.Features.Extraction;
using SeedForge.Features.Ingestion;
using SeedForge.Features.Scoring;
using SeedForge.Features.Segmentation;
using SeedForge.Pipeline;
using SeedForge.Services.Queues;
using SeedForge.UnitTests.Fakes;
using SeedForge.Workers;

namespace SeedForge.UnitTests
{
    /// <summary>The Processing worker's extracted iteration: a claimed video drains to enqueued ConceptJobs, and a thrown error reschedules with backoff.</summary>
    public class ProcessingWorkerTests : IDisposable
    {
        private readonly SliceTestHarness _h = new();
        private readonly WorkerOptions _opts = new() { MaxAttempts = 5, BackoffBaseSeconds = 30 };

        private (ProcessingIteration Iteration, VideoQueue Queue) Build(
            FakeLlmClient fake, ApplicationDbContext db, FakeApifyIngestionService apify)
        {
            var pipelineOptions = Options.Create(new PipelineOptions { ScoreThreshold = 0.6 });
            var ingest = new IngestTranscriptHandler(db, apify, NullLogger<IngestTranscriptHandler>.Instance);
            var queue = new VideoQueue(db, Options.Create(_opts), new WorkerControl(), NullLogger<VideoQueue>.Instance);
            var conceptQueue = new ConceptQueue(db, Options.Create(_opts), new WorkerControl(), NullLogger<ConceptQueue>.Instance);
            var pipeline = new PipelineRunner(
                db,
                ingest,
                new SegmentTranscriptHandler(db, fake, _h.Resolver, NullLogger<SegmentTranscriptHandler>.Instance),
                new ExtractIdeasHandler(db, fake, _h.Resolver, NullLogger<ExtractIdeasHandler>.Instance),
                new ScoreIdeasHandler(db, fake, _h.Resolver, pipelineOptions, NullLogger<ScoreIdeasHandler>.Instance),
                new BuildConceptHandler(db, fake, _h.Resolver, NullLogger<BuildConceptHandler>.Instance),
                conceptQueue,
                NullLogger<PipelineRunner>.Instance);
            var iteration = new ProcessingIteration(queue, pipeline, NullLogger<ProcessingIteration>.Instance);
            return (iteration, queue);
        }

        [Fact]
        public async Task ProcessOnceAsync_drains_a_pending_video_into_enqueued_concept_jobs()
        {
            const string text = "First we cover terraforming Mars over generations. " +
                                "Then we explore a rogue AI that runs a starship alone.";
            var apify = FakeApifyIngestionService.WithTranscript("abc12345678", text);
            var fake = new FakeLlmClient()
                .SetStructured(new SegmentationResponse(new()
                {
                    new SegmentBoundaryDto(0, "Terraforming", "First we cover terraforming Mars"),
                    new SegmentBoundaryDto(1, "Rogue AI", "Then we explore a rogue AI"),
                }))
                .SetStructured(new ExtractIdeasResponse(new() { new ThinIdeaDto("A premise from this segment.") }))
                .SetStructured(new ScoreIdeasResponse(new()
                {
                    new IdeaScoreDto(0, 0.9, 0.9, 0.9, 0.9),
                    new IdeaScoreDto(1, 0.9, 0.9, 0.9, 0.9),
                }));

            using var db = _h.NewDb();
            var (iteration, queue) = Build(fake, db, apify);
            var videoId = await queue.EnqueueAsync("abc12345678");

            var processed = await iteration.ProcessOnceAsync(CancellationToken.None);

            Assert.True(processed);
            using var read = _h.NewDb();
            Assert.Equal(VideoJobStatus.Done, read.Videos.Single(v => v.Id == videoId).Status);
            Assert.Equal(2, read.ConceptJobs.Count(j => j.Status == ConceptJobStatus.Pending));
            Assert.Empty(read.Concepts); // builds nothing
        }

        [Fact]
        public async Task ProcessOnceAsync_returns_false_when_the_queue_is_empty()
        {
            var fake = new FakeLlmClient();
            using var db = _h.NewDb();
            var (iteration, _) = Build(fake, db, FakeApifyIngestionService.NoTranscript("unused00000"));

            Assert.False(await iteration.ProcessOnceAsync(CancellationToken.None));
        }

        [Fact]
        public async Task ProcessOnceAsync_reschedules_with_backoff_on_a_thrown_error_without_crashing()
        {
            const string text = "First we cover terraforming Mars over generations. " +
                                "Then we explore a rogue AI that runs a starship alone.";
            var apify = FakeApifyIngestionService.WithTranscript("abc12345678", text);
            // Segmentation + extraction configured, but NO scoring response ⇒ the scorer throws mid-pipeline.
            var fake = new FakeLlmClient()
                .SetStructured(new SegmentationResponse(new()
                {
                    new SegmentBoundaryDto(0, "Terraforming", "First we cover terraforming Mars"),
                }))
                .SetStructured(new ExtractIdeasResponse(new() { new ThinIdeaDto("A premise from this segment.") }));

            using var db = _h.NewDb();
            var (iteration, queue) = Build(fake, db, apify);
            var videoId = await queue.EnqueueAsync("abc12345678");

            // Must not throw: the error is caught and the job is rescheduled.
            var processed = await iteration.ProcessOnceAsync(CancellationToken.None);

            Assert.True(processed);
            using var read = _h.NewDb();
            var video = read.Videos.Single(v => v.Id == videoId);
            Assert.Equal(VideoJobStatus.Pending, video.Status);
            Assert.Equal(1, video.AttemptCount);
            Assert.True(video.NextAttemptUtc > DateTime.UtcNow);
        }

        public void Dispose() => _h.Dispose();
    }
}
