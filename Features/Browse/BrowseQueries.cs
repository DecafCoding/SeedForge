using System.Text.Json;
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

    /// <summary>One concept built from this video's ideas, for the Video Details concept table (links to <c>/concepts</c>).</summary>
    public sealed record ConceptSummary(
        int Id, int IdeaId, string Title, string Genre,
        bool IsActive, bool IsStale, string Model, DateTime CreatedAtUtc);

    /// <summary>One LLM call in this video's run, for the Video Details AI trace (filtered by the video's ideas' correlation ids).</summary>
    public sealed record AiCallSummary(
        int Id, string Stage, Domain.ModelSlot Slot, string Model,
        int TotalTokens, double EstimatedCost, bool Success, DateTime CreatedAtUtc);

    /// <summary>
    /// The full single-video view-model for <c>/videos/{id}</c>: identity (<paramref name="Url"/>/<paramref name="Title"/>/
    /// <paramref name="Channel"/>/<paramref name="DurationSeconds"/> + Phase 8 metadata), lifecycle
    /// (<paramref name="Status"/>/<paramref name="DateAddedUtc"/>/<paramref name="DateProcessedUtc"/>/attempts/cost/error),
    /// pipeline yield (<paramref name="SegmentCount"/>, idea split <paramref name="Passed"/>/<paramref name="Failed"/>/
    /// <paramref name="Unscored"/>, <paramref name="Concepts"/>), and the AI trace (<paramref name="AiCalls"/> + totals).
    /// <paramref name="DurationSeconds"/> is <c>Video.DurationSeconds</c> (Phase 8) or, for rows ingested earlier, parsed
    /// from <c>Transcript.RawDatasetItemJson</c>; null when neither has it. <paramref name="IsProcessedDerived"/> is true
    /// when <paramref name="DateProcessedUtc"/> is derived from the AI log/transcript rather than a stored timestamp.
    /// </summary>
    public sealed record VideoDetail(
        int Id, string Url, string? Title, string? Channel, int? DurationSeconds,
        Domain.VideoJobStatus Status,
        DateTime DateAddedUtc, DateTime? DateProcessedUtc, bool IsProcessedDerived,
        double? ApifyCostUnits, int AttemptCount, string? ErrorMessage,
        int SegmentCount, int IdeaCount, int Passed, int Failed, int Unscored,
        IReadOnlyList<ConceptSummary> Concepts,
        IReadOnlyList<AiCallSummary> AiCalls, int TotalTokens, double TotalCost,
        long? ViewCount, DateTime? PublishedAtUtc, string? ThumbnailUrl);

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

        /// <summary>
        /// Everything known about one <see cref="Domain.Video"/> for the <c>/videos/{id}</c> detail page, or null when the
        /// id is unknown. Resolves the channel via <see cref="Domain.Video.ChannelId"/> (left join) falling back to
        /// <see cref="Domain.Transcript.ChannelName"/>; the duration from <see cref="Domain.Video.DurationSeconds"/>
        /// (Phase 8) or, for pre-Phase-8 rows, parsed from <see cref="Domain.Transcript.RawDatasetItemJson"/>; the idea
        /// split from each idea's <em>latest</em> <see cref="Domain.IdeaScore"/> verdict; and the AI trace from every
        /// <see cref="Domain.AiCallLog"/> sharing one of this video's ideas' correlation ids. The processed-time prefers
        /// the stored <see cref="Domain.Video.ProcessedAtUtc"/> and, when absent, derives it as the latest AI-call time
        /// (else the transcript's creation time) — flagged via <see cref="VideoDetail.IsProcessedDerived"/>.
        /// </summary>
        public async Task<VideoDetail?> VideoDetailAsync(int id, CancellationToken ct = default)
        {
            var v = await db.Videos.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (v is null) return null;

            var transcript = await db.Transcripts.FirstOrDefaultAsync(t => t.VideoId == id, ct);

            // Channel: left join Video.ChannelId -> Channel.Title, falling back to the transcript's channel name.
            string? channel = null;
            if (v.ChannelId is int channelId)
            {
                channel = await db.Channels.Where(c => c.Id == channelId).Select(c => c.Title).FirstOrDefaultAsync(ct);
            }
            channel ??= transcript?.ChannelName;

            // Duration: Phase 8 column wins; otherwise parse the preserved raw Apify item (interim).
            int? duration = v.DurationSeconds
                ?? (transcript is null ? null : TryParseDuration(transcript.RawDatasetItemJson));

            // Yield: segments and ideas come off this video's single transcript.
            int segmentCount = 0;
            var ideas = new List<Domain.Idea>();
            if (transcript is not null)
            {
                var segmentIds = await db.Segments.Where(s => s.TranscriptId == transcript.Id)
                    .Select(s => s.Id).ToListAsync(ct);
                segmentCount = segmentIds.Count;
                ideas = await db.Ideas.Where(i => segmentIds.Contains(i.SegmentId)).ToListAsync(ct);
            }
            var ideaIds = ideas.Select(i => i.Id).ToList();
            var correlationIds = ideas.Select(i => i.CorrelationId)
                .Where(c => !string.IsNullOrEmpty(c)).Distinct().ToHashSet();

            // Latest-score verdict per idea (materialize then group — GroupBy+First doesn't always translate).
            var latestPass = (await db.IdeaScores.Where(s => ideaIds.Contains(s.IdeaId)).ToListAsync(ct))
                .GroupBy(s => s.IdeaId)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(s => s.Id).First().PassedThreshold);
            int passed = ideaIds.Count(i => latestPass.TryGetValue(i, out var p) && p);
            int failed = ideaIds.Count(i => latestPass.TryGetValue(i, out var p) && !p);
            int unscored = ideaIds.Count - passed - failed;

            // Concepts built from this video's ideas, newest-first.
            var concepts = (await db.Concepts.Where(c => ideaIds.Contains(c.IdeaId)).ToListAsync(ct))
                .OrderByDescending(c => c.Id)
                .Select(c => new ConceptSummary(c.Id, c.IdeaId, c.Title, c.Genre, c.IsActive, c.IsStale, c.Model, c.CreatedAtUtc))
                .ToList();

            // AI trace: every call sharing one of this video's correlation ids, newest-first.
            var calls = correlationIds.Count == 0
                ? new List<Domain.AiCallLog>()
                : await db.AiCallLogs.Where(l => correlationIds.Contains(l.CorrelationId)).ToListAsync(ct);
            var aiCalls = calls.OrderByDescending(l => l.Id)
                .Select(l => new AiCallSummary(l.Id, l.Stage, l.Slot, l.Model, l.TotalTokens, l.EstimatedCost, l.Success, l.CreatedAtUtc))
                .ToList();
            int totalTokens = calls.Sum(l => l.TotalTokens);
            double totalCost = calls.Sum(l => l.EstimatedCost);

            // Processed-time: prefer the stored Video.ProcessedAtUtc; for rows processed before it existed, derive it from
            // the latest AI call, else the transcript's creation time (flagged derived so the UI labels it honestly).
            DateTime? processed = v.ProcessedAtUtc;
            bool derived = processed is null;
            if (derived)
            {
                processed = calls.Count > 0
                    ? calls.Max(l => l.CreatedAtUtc)
                    : transcript?.CreatedAtUtc;
            }

            return new VideoDetail(
                v.Id, v.Url, v.Title, channel, duration,
                v.Status,
                v.CreatedAtUtc, processed, derived,
                v.ApifyCostUnits ?? transcript?.ApifyCostUnits, v.AttemptCount, v.ErrorMessage,
                segmentCount, ideaIds.Count, passed, failed, unscored,
                concepts,
                aiCalls, totalTokens, totalCost,
                v.ViewCount, v.PublishedAtUtc, v.ThumbnailUrl);
        }

        /// <summary>
        /// Interim duration parse for pre-Phase-8 rows: probes the verbatim Apify item for a likely duration key,
        /// accepting either a numeric seconds value or a clock string ("1:02:03" ⇒ 3723). Null when absent/unparseable.
        /// </summary>
        private static int? TryParseDuration(string rawJson)
        {
            if (string.IsNullOrWhiteSpace(rawJson)) return null;
            try
            {
                using var doc = JsonDocument.Parse(rawJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
                foreach (var key in new[] { "duration", "lengthSeconds", "durationSeconds" })
                {
                    if (!doc.RootElement.TryGetProperty(key, out var el)) continue;
                    return el.ValueKind switch
                    {
                        JsonValueKind.Number when el.TryGetInt32(out var n) => n,
                        JsonValueKind.String => ParseClock(el.GetString()),
                        _ => null,
                    };
                }
            }
            catch (JsonException)
            {
                // Malformed raw item — treat as "no duration" rather than crashing the page.
            }
            return null;
        }

        /// <summary>Parses a "ss", "mm:ss", or "hh:mm:ss" clock string to total seconds; null on anything unexpected.</summary>
        private static int? ParseClock(string? clock)
        {
            if (string.IsNullOrWhiteSpace(clock)) return null;
            var parts = clock.Split(':');
            if (parts.Length is < 1 or > 3) return null;
            int total = 0;
            foreach (var part in parts)
            {
                if (!int.TryParse(part, out var n) || n < 0) return null;
                total = total * 60 + n;
            }
            return total;
        }
    }
}
