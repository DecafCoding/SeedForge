using Microsoft.EntityFrameworkCore;
using SeedForge.Data;

namespace SeedForge.Features.Browse
{
    /// <summary>
    /// One read-only row in the pool-wide ideas table: an idea, its source-video lineage, and the latest score.
    /// <paramref name="VideoId"/>/<paramref name="VideoUrl"/>/<paramref name="VideoTitle"/> are null for ideas from a
    /// pasted transcript (no source video). Score fields (and <paramref name="Passed"/>) are null when the idea is unscored.
    /// </summary>
    public sealed record IdeaRow(
        int Id, string Premise,
        int? VideoId, string? VideoUrl, string? VideoTitle,
        double? Novelty, double? Coherence, double? SciFiPotential, double? FormulaFit, double? Mean,
        bool? Passed, DateTime CreatedAtUtc);

    /// <summary>
    /// One read-only row in the pool-wide videos table: a source video and its pipeline yield. <paramref name="Passed"/>/
    /// <paramref name="Failed"/> split the idea pool by each idea's <em>latest</em> <see cref="Domain.IdeaScore"/> verdict;
    /// <paramref name="Unscored"/> is the remainder (<paramref name="IdeaCount"/> = Passed + Failed + Unscored).
    /// <paramref name="ConceptCount"/> is every concept built from this video's ideas; <paramref name="ActiveConceptCount"/>
    /// counts only the currently-active versions.
    /// </summary>
    public sealed record VideoRow(
        int Id, string Url, string? Title, Domain.VideoJobStatus Status,
        int IdeaCount, int Passed, int Failed, int Unscored,
        int ConceptCount, int ActiveConceptCount, DateTime CreatedAtUtc);

    /// <summary>
    /// Shared read-only projections for the Phase 9–11 browse pages (Ideas · Videos · Video Details). Queries via the
    /// context directly (no repository) and shapes in memory — the data volume for a single-user tool is small and
    /// SQLite/EF will not translate every GroupBy+First shape. No write/command paths live here.
    /// </summary>
    public sealed class BrowseQueries(ApplicationDbContext db)
    {
        /// <summary>
        /// Every extracted idea with its source-video lineage and latest score, newest-first. Joins
        /// Idea → Segment → Transcript → Video (left join — pasted-transcript ideas have no video and survive with null
        /// video fields). The latest <see cref="Domain.IdeaScore"/> per idea (by descending Id) supplies the four axes,
        /// their mean, and the pass/fail verdict; an unscored idea yields null score fields and a null <c>Passed</c>.
        /// </summary>
        public async Task<IReadOnlyList<IdeaRow>> IdeaRowsAsync(CancellationToken ct = default)
        {
            // Lineage: Idea -> Segment -> Transcript -> Video (Video may be null for pasted transcripts).
            var ideas = await (
                from i in db.Ideas
                join seg in db.Segments on i.SegmentId equals seg.Id
                join t in db.Transcripts on seg.TranscriptId equals t.Id
                join v in db.Videos on t.VideoId equals v.Id into vj
                from v in vj.DefaultIfEmpty()                // left join — keep pasted-transcript ideas
                select new { i, TranscriptTitle = t.Title, VideoId = (int?)v.Id, VideoUrl = v.Url, VideoTitle = v.Title }
            ).ToListAsync(ct);

            // Latest score per idea — materialize then group in memory (GroupBy+First doesn't always translate).
            var byIdea = (await db.IdeaScores.ToListAsync(ct))
                .GroupBy(s => s.IdeaId)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(s => s.Id).First());

            return ideas.Select(x =>
            {
                byIdea.TryGetValue(x.i.Id, out var s);
                double? mean = s is null ? null
                    : (s.Novelty + s.Coherence + s.SciFiPotential + s.FormulaFit) / 4.0;
                return new IdeaRow(
                    x.i.Id, x.i.Premise,
                    x.VideoId, x.VideoUrl, x.VideoTitle ?? x.TranscriptTitle,
                    s?.Novelty, s?.Coherence, s?.SciFiPotential, s?.FormulaFit, mean,
                    s?.PassedThreshold,                      // null ⇒ unscored
                    x.i.CreatedAtUtc);
            }).OrderByDescending(r => r.CreatedAtUtc).ThenByDescending(r => r.Id).ToList();
        }

        /// <summary>
        /// Every video with its pipeline yield, newest-first. For each <see cref="Domain.Video"/> walks
        /// Idea → Segment → Transcript → Video to attribute ideas (a <see cref="Domain.Concept"/> has no direct video FK,
        /// so it is attributed the same way through its idea). Each idea is split by its <em>latest</em>
        /// <see cref="Domain.IdeaScore"/> verdict into passed/failed/unscored; concepts are counted total and active.
        /// A <see cref="Domain.VideoJobStatus.NoTranscript"/>/<see cref="Domain.VideoJobStatus.ProcessedNoIdeas"/> video
        /// with no ideas yields zero counts — a legitimate outcome carried by <c>Status</c>, not an error.
        /// </summary>
        public async Task<IReadOnlyList<VideoRow>> VideoRowsAsync(CancellationToken ct = default)
        {
            var videos = await db.Videos.OrderByDescending(v => v.Id).ToListAsync(ct);

            // Map ideaId -> videoId via Idea -> Segment -> Transcript -> Video (only transcripts tied to a video).
            var ideaToVideo = await (
                from i in db.Ideas
                join seg in db.Segments on i.SegmentId equals seg.Id
                join t in db.Transcripts on seg.TranscriptId equals t.Id
                where t.VideoId != null
                select new { IdeaId = i.Id, VideoId = t.VideoId!.Value }
            ).ToListAsync(ct);

            // Latest score's verdict per idea — materialize then group (GroupBy+First doesn't always translate).
            var latestPass = (await db.IdeaScores.ToListAsync(ct))
                .GroupBy(s => s.IdeaId)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(s => s.Id).First().PassedThreshold);

            var ideaIdsByVideo = ideaToVideo
                .GroupBy(x => x.VideoId)
                .ToDictionary(g => g.Key, g => g.Select(x => x.IdeaId).ToList());
            var videoIdByIdea = ideaToVideo.ToDictionary(x => x.IdeaId, x => x.VideoId);

            var concepts = await db.Concepts.ToListAsync(ct);

            return videos.Select(v =>
            {
                ideaIdsByVideo.TryGetValue(v.Id, out var ideaIds);
                ideaIds ??= new();
                int passed = ideaIds.Count(id => latestPass.TryGetValue(id, out var p) && p);
                int failed = ideaIds.Count(id => latestPass.TryGetValue(id, out var p) && !p);
                int unscored = ideaIds.Count - passed - failed;
                var vConcepts = concepts.Where(c => videoIdByIdea.TryGetValue(c.IdeaId, out var vid) && vid == v.Id).ToList();
                return new VideoRow(
                    v.Id, v.Url, v.Title, v.Status,
                    ideaIds.Count, passed, failed, unscored,
                    vConcepts.Count, vConcepts.Count(c => c.IsActive), v.CreatedAtUtc);
            }).ToList();
        }
    }
}
