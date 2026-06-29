using Microsoft.EntityFrameworkCore;
using SeedForge.Data;
using SeedForge.Domain;
using SeedForge.Services.Apify;

namespace SeedForge.Features.Maintenance
{
    /// <summary>How many videos were scanned and how many gained metadata in a backfill pass.</summary>
    public sealed record BackfillVideoMetadataResult(int Scanned, int Updated);

    /// <summary>
    /// One-shot maintenance slice: re-parses the verbatim Apify item already stored in
    /// <see cref="Transcript.RawDatasetItemJson"/> to populate metadata on videos ingested before Phase 8 — at zero
    /// extra API cost (no Apify re-fetch). Idempotent and re-runnable: it only touches rows still at
    /// <see cref="MetadataSource.None"/>, so it never overwrites Apify- or YouTube-sourced metadata.
    /// </summary>
    public sealed class BackfillVideoMetadataHandler(ApplicationDbContext db, ILogger<BackfillVideoMetadataHandler> log)
    {
        public async Task<BackfillVideoMetadataResult> HandleAsync(CancellationToken ct = default)
        {
            // Candidates: not-yet-sourced videos whose transcript still holds the raw Apify item.
            var candidates = await (
                from v in db.Videos
                where v.MetadataSource == MetadataSource.None
                join t in db.Transcripts on v.Id equals t.VideoId
                where t.RawDatasetItemJson != ""
                select new { Video = v, t.RawDatasetItemJson }
            ).ToListAsync(ct);

            var now = DateTime.UtcNow;
            var updated = 0;
            foreach (var c in candidates)
            {
                var metadata = ApifyMetadataParser.TryParse(c.RawDatasetItemJson);
                if (metadata is { HasAnyValue: true })
                {
                    metadata.ApplyTo(c.Video, now);
                    updated++;
                }
            }

            if (updated > 0) await db.SaveChangesAsync(ct);

            log.LogInformation("Backfilled video metadata: {Updated} of {Scanned} candidate(s) updated", updated, candidates.Count);
            return new BackfillVideoMetadataResult(candidates.Count, updated);
        }
    }
}
