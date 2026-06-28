using SeedForge.Data;
using SeedForge.Domain;
using SeedForge.Features.Concepts;
using SeedForge.Features.Extraction;
using SeedForge.Features.Scoring;
using SeedForge.Features.Segmentation;

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

    /// <summary>
    /// Driving-adapter orchestrator: creates the source <see cref="Transcript"/> from pasted text and threads one
    /// correlation id through the four slices (segment → extract → score → build). Lives outside Services/Features
    /// because it is the single place the otherwise-independent slices are composed.
    /// </summary>
    public sealed class PipelineRunner(
        ApplicationDbContext db,
        SegmentTranscriptHandler segment,
        ExtractIdeasHandler extract,
        ScoreIdeasHandler score,
        BuildConceptHandler build,
        ILogger<PipelineRunner> log)
    {
        public async Task<PipelineRunResult> RunAsync(string transcriptText, string? title, CancellationToken ct = default)
        {
            var correlationId = Guid.NewGuid().ToString("N");
            log.LogInformation("Pipeline run {Corr} started", correlationId);

            // 1) Persist the immutable source transcript (no backing video for pasted text).
            var transcript = new Transcript
            {
                VideoId = null,
                PlainText = transcriptText ?? string.Empty,
                Title = title,
                CreatedAtUtc = DateTime.UtcNow,
            };
            db.Transcripts.Add(transcript);
            await db.SaveChangesAsync(ct);

            // 2) Segment the transcript.
            var segmentIds = await segment.HandleAsync(new(transcript.Id, correlationId), ct);

            // 3) Extract ideas per segment.
            var ideaIds = new List<int>();
            foreach (var segmentId in segmentIds)
            {
                ideaIds.AddRange(await extract.HandleAsync(new(segmentId, correlationId), ct));
            }

            // 4) Score all ideas in a single pass; survivors pass the threshold.
            var survivorIds = await score.HandleAsync(new(ideaIds, correlationId), ct);

            // 5) Build a concept for each survivor only.
            var conceptIds = new List<int>();
            foreach (var survivorId in survivorIds)
            {
                conceptIds.Add(await build.HandleAsync(new(survivorId, correlationId), ct));
            }

            log.LogInformation(
                "Pipeline run {Corr} done: {Segments} segment(s), {Ideas} idea(s), {Survivors} survivor(s), {Concepts} concept(s)",
                correlationId, segmentIds.Count, ideaIds.Count, survivorIds.Count, conceptIds.Count);

            return new PipelineRunResult(transcript.Id, segmentIds, ideaIds, survivorIds, conceptIds, correlationId);
        }
    }
}
