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
    }
}
