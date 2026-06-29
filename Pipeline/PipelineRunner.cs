using Microsoft.EntityFrameworkCore;
using SeedForge.Data;
using SeedForge.Domain;
using SeedForge.Features.Concepts;
using SeedForge.Features.Extraction;
using SeedForge.Features.Ingestion;
using SeedForge.Features.Scoring;
using SeedForge.Features.Segmentation;
using SeedForge.Services.Queues;

namespace SeedForge.Pipeline
{
    /// <summary>The ids produced by one end-to-end pipeline run, plus the correlation id tying its AI calls together.</summary>
    public sealed record PipelineRunResult(
        int TranscriptId,
        IReadOnlyList<int> SegmentIds,
        IReadOnlyList<int> IdeaIds,
        IReadOnlyList<int> SurvivorIds,
        IReadOnlyList<int> ConceptIds,
        string CorrelationId);

    /// <summary>The outcome of a URL run: the ingested video + status, and the pipeline result when one ran (null when NoTranscript/Failed).</summary>
    public sealed record PipelineUrlResult(
        int VideoId,
        int? TranscriptId,
        VideoJobStatus Status,
        PipelineRunResult? Pipeline,
        string CorrelationId);

    /// <summary>
    /// The outcome of the worker-facing processing path (stops at scoring): the survivors found and the ConceptJobs
    /// enqueued for them. <see cref="Status"/> is the intended video outcome (Done / ProcessedNoIdeas /
    /// NoTranscript / Failed) for the caller to record on the queue. No concepts are built here.
    /// </summary>
    public sealed record ProcessVideoResult(
        int VideoId,
        int? TranscriptId,
        VideoJobStatus Status,
        IReadOnlyList<int> SurvivorIds,
        IReadOnlyList<int> EnqueuedConceptJobIds,
        string CorrelationId);

    /// <summary>
    /// Driving-adapter orchestrator: threads one correlation id through ingestion (URL path) and the four slices
    /// (segment → extract → score → build). The paste-text and URL paths converge on <see cref="RunFromTranscriptAsync"/>
    /// so the four-stage sequence is never duplicated. Lives outside Services/Features because it is the single place
    /// the otherwise-independent slices are composed.
    /// </summary>
    public sealed class PipelineRunner(
        ApplicationDbContext db,
        IngestTranscriptHandler ingest,
        SegmentTranscriptHandler segment,
        ExtractIdeasHandler extract,
        ScoreIdeasHandler score,
        BuildConceptHandler build,
        ConceptQueue conceptQueue,
        ILogger<PipelineRunner> log)
    {
        /// <summary>Paste-text entry: persists a video-less <see cref="Transcript"/> then runs the four-stage pipeline on it.</summary>
        public async Task<PipelineRunResult> RunAsync(string transcriptText, string? title, CancellationToken ct = default)
        {
            var correlationId = NewCorrelationId();

            // Persist the immutable source transcript (no backing video for pasted text).
            var transcript = new Transcript
            {
                VideoId = null,
                PlainText = transcriptText ?? string.Empty,
                Title = title,
                CreatedAtUtc = DateTime.UtcNow,
            };
            db.Transcripts.Add(transcript);
            await db.SaveChangesAsync(ct);

            return await RunFromTranscriptAsync(transcript.Id, correlationId, ct);
        }

        /// <summary>URL entry: ingests the video then, if a transcript was found, runs the pipeline on it under the same correlation id.</summary>
        public async Task<PipelineUrlResult> RunFromUrlAsync(string urlOrId, CancellationToken ct = default)
        {
            var correlationId = NewCorrelationId();
            log.LogInformation("URL pipeline run {Corr} started for {Url}", correlationId, urlOrId);

            var ingested = await ingest.HandleAsync(new(urlOrId, correlationId), ct);

            if (ingested.Status != VideoJobStatus.Done || ingested.TranscriptId is null)
            {
                // NoTranscript / Failed: there is nothing to segment — the pipeline does not run.
                log.LogInformation("Ingestion of {Url} ended {Status}; pipeline skipped (corr {Corr})",
                    urlOrId, ingested.Status, correlationId);
                return new PipelineUrlResult(ingested.VideoId, ingested.TranscriptId, ingested.Status, null, correlationId);
            }

            var pipeline = await RunFromTranscriptAsync(ingested.TranscriptId.Value, correlationId, ct);
            return new PipelineUrlResult(ingested.VideoId, ingested.TranscriptId, ingested.Status, pipeline, correlationId);
        }

        /// <summary>The reusable four-stage core: segment → extract (per segment) → score (one pass) → build (per survivor).</summary>
        public async Task<PipelineRunResult> RunFromTranscriptAsync(int transcriptId, string correlationId, CancellationToken ct = default)
        {
            log.LogInformation("Pipeline run {Corr} started on transcript {TranscriptId}", correlationId, transcriptId);

            // Stages 1–3: segment → extract → score (the processing core, shared with the worker path).
            var (segmentIds, ideaIds, survivorIds) = await RunProcessingFromTranscriptAsync(transcriptId, correlationId, ct);

            // 4) Build a concept for each survivor only (inline — the manual page wants immediate feedback).
            var conceptIds = new List<int>();
            foreach (var survivorId in survivorIds)
            {
                conceptIds.Add(await build.HandleAsync(new(survivorId, correlationId), ct));
            }

            log.LogInformation(
                "Pipeline run {Corr} done: {Segments} segment(s), {Ideas} idea(s), {Survivors} survivor(s), {Concepts} concept(s)",
                correlationId, segmentIds.Count, ideaIds.Count, survivorIds.Count, conceptIds.Count);

            return new PipelineRunResult(transcriptId, segmentIds, ideaIds, survivorIds, conceptIds, correlationId);
        }

        /// <summary>
        /// Worker-facing entry: process one queued video to the scoring seam and enqueue a ConceptJob per survivor —
        /// it ingests the transcript (if not yet present), runs segment → extract → score, and <strong>builds no
        /// concepts</strong> (a separate Concept worker drains the enqueued jobs on its own cadence). Returns the
        /// intended video status for the caller to record on the queue: Done (survivors &gt; 0) / ProcessedNoIdeas
        /// (zero survivors) / the ingest-set NoTranscript or Failed when there was nothing to process.
        /// </summary>
        public async Task<ProcessVideoResult> ProcessVideoJobAsync(int videoId, CancellationToken ct = default)
        {
            var correlationId = NewCorrelationId();
            var video = await db.Videos.FirstOrDefaultAsync(v => v.Id == videoId, ct)
                ?? throw new InvalidOperationException($"Video {videoId} not found");

            log.LogInformation("Processing video job {VideoId} (corr {Corr})", videoId, correlationId);

            // Ensure a transcript: reuse an already-ingested one, else fetch via the ingestion slice (idempotent).
            var transcript = await db.Transcripts.FirstOrDefaultAsync(t => t.VideoId == videoId, ct);
            int? transcriptId = transcript?.Id;
            if (transcriptId is null)
            {
                var ingested = await ingest.HandleAsync(new(video.Url, correlationId), ct);
                if (ingested.Status != VideoJobStatus.Done || ingested.TranscriptId is null)
                {
                    // NoTranscript / Failed: nothing to segment — the ingest-set status stands, no scoring, no enqueue.
                    log.LogInformation("Video {VideoId} ingest ended {Status}; processing skipped (corr {Corr})",
                        videoId, ingested.Status, correlationId);
                    return new ProcessVideoResult(
                        videoId, ingested.TranscriptId, ingested.Status,
                        Array.Empty<int>(), Array.Empty<int>(), correlationId);
                }
                transcriptId = ingested.TranscriptId;
            }

            // Stages 1–3 only (stops at scoring).
            var (_, _, survivorIds) = await RunProcessingFromTranscriptAsync(transcriptId.Value, correlationId, ct);

            // Enqueue one Auto ConceptJob per survivor; the Concept worker develops them later.
            var enqueued = new List<int>();
            foreach (var survivorId in survivorIds)
            {
                enqueued.Add(await conceptQueue.EnqueueAsync(survivorId, ConceptTrigger.Auto, ct: ct));
            }

            // Zero survivors is a legitimate outcome (ProcessedNoIdeas), never Failed.
            var status = survivorIds.Count > 0 ? VideoJobStatus.Done : VideoJobStatus.ProcessedNoIdeas;
            log.LogInformation("Video {VideoId} processed: {Survivors} survivor(s) ⇒ {Enqueued} ConceptJob(s), status {Status} (corr {Corr})",
                videoId, survivorIds.Count, enqueued.Count, status, correlationId);

            return new ProcessVideoResult(videoId, transcriptId, status, survivorIds, enqueued, correlationId);
        }

        /// <summary>
        /// The processing core (stages 1–3): segment → extract (per segment) → score (one pass). Shared by the manual
        /// inline-build path and the worker enqueue path so the upstream sequence is never duplicated.
        /// </summary>
        private async Task<(IReadOnlyList<int> SegmentIds, IReadOnlyList<int> IdeaIds, IReadOnlyList<int> SurvivorIds)>
            RunProcessingFromTranscriptAsync(int transcriptId, string correlationId, CancellationToken ct)
        {
            // 1) Segment the transcript.
            var segmentIds = await segment.HandleAsync(new(transcriptId, correlationId), ct);

            // 2) Extract ideas per segment.
            var ideaIds = new List<int>();
            foreach (var segmentId in segmentIds)
            {
                ideaIds.AddRange(await extract.HandleAsync(new(segmentId, correlationId), ct));
            }

            // 3) Score all ideas in a single pass; survivors pass the threshold.
            var survivorIds = await score.HandleAsync(new(ideaIds, correlationId), ct);

            return (segmentIds, ideaIds, survivorIds);
        }

        private static string NewCorrelationId() => Guid.NewGuid().ToString("N");
    }
}
