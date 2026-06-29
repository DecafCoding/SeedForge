using Microsoft.EntityFrameworkCore;
using SeedForge.Data;
using SeedForge.Domain;
using SeedForge.Services.Apify;
using SeedForge.Services.YouTube;

namespace SeedForge.Features.Ingestion
{
    public sealed record IngestTranscriptRequest(string UrlOrId, string CorrelationId);

    /// <summary>The persisted outcome of an ingestion: the source <see cref="Video"/>, its <see cref="Transcript"/> (when one exists), and the recorded status.</summary>
    public sealed record IngestTranscriptResult(int VideoId, int? TranscriptId, VideoJobStatus Status);

    /// <summary>
    /// Ingestion slice: turns a YouTube URL/id into immutable <see cref="Video"/> + <see cref="Transcript"/> sources,
    /// recording Done / NoTranscript / Failed distinctly. Idempotent on <see cref="Video.YouTubeVideoId"/> — an
    /// already-transcribed video is reused, never re-fetched (Apify runs cost money).
    /// </summary>
    public sealed class IngestTranscriptHandler(
        ApplicationDbContext db, IApifyIngestionService apify, ILogger<IngestTranscriptHandler> log)
    {
        public async Task<IngestTranscriptResult> HandleAsync(IngestTranscriptRequest req, CancellationToken ct)
        {
            // Reject malformed / non-YouTube input before any Apify call or write.
            if (!YouTubeUrl.TryGetVideoId(req.UrlOrId, out var videoId))
            {
                throw new ArgumentException($"'{req.UrlOrId}' is not a recognizable YouTube URL or video id.", nameof(req));
            }

            // Idempotent on the YouTube id (uniquely indexed): never write a second Video for one id.
            var video = await db.Videos.FirstOrDefaultAsync(v => v.YouTubeVideoId == videoId, ct);
            if (video is not null)
            {
                var existing = await db.Transcripts.FirstOrDefaultAsync(t => t.VideoId == video.Id, ct);
                if (existing is not null)
                {
                    log.LogInformation("Video {VideoId} already ingested; reusing transcript {TranscriptId}",
                        videoId, existing.Id);
                    return new IngestTranscriptResult(video.Id, existing.Id, video.Status);
                }
            }
            else
            {
                video = new Video
                {
                    YouTubeVideoId = videoId,
                    Url = YouTubeUrl.WatchUrl(videoId),
                    Status = VideoJobStatus.InProgress,
                    CreatedAtUtc = DateTime.UtcNow,
                };
                db.Videos.Add(video);
                await db.SaveChangesAsync(ct);
            }

            // (Re-)attempt the fetch into the existing/new Video row.
            video.Status = VideoJobStatus.InProgress;
            video.ErrorMessage = null;

            try
            {
                var ing = await apify.FetchAsync(req.UrlOrId, ct);

                if (ing.HadTranscript)
                {
                    var transcript = new Transcript
                    {
                        VideoId = video.Id,
                        PlainText = ing.Text ?? string.Empty,
                        RawDatasetItemJson = ing.RawItemJson,
                        Title = ing.Title,
                        ChannelName = ing.ChannelName,
                        ApifyCostUnits = ing.CostUnits,
                        CreatedAtUtc = DateTime.UtcNow,
                    };
                    db.Transcripts.Add(transcript);

                    video.Status = VideoJobStatus.Done;
                    video.Title = ing.Title;
                    video.ApifyCostUnits = ing.CostUnits;
                    ApplyMetadata(video, ing.Metadata);
                    await db.SaveChangesAsync(ct);

                    log.LogInformation("Ingested video {VideoId} → transcript {TranscriptId} (corr {Corr})",
                        videoId, transcript.Id, req.CorrelationId);
                    return new IngestTranscriptResult(video.Id, transcript.Id, VideoJobStatus.Done);
                }

                // A present item with no captions is a legitimate outcome, not a failure.
                video.Status = VideoJobStatus.NoTranscript;
                video.Title = ing.Title;
                video.ApifyCostUnits = ing.CostUnits;
                ApplyMetadata(video, ing.Metadata);
                await db.SaveChangesAsync(ct);

                log.LogInformation("Video {VideoId} has no transcript (corr {Corr})", videoId, req.CorrelationId);
                return new IngestTranscriptResult(video.Id, null, VideoJobStatus.NoTranscript);
            }
            catch (ApifyException ex)
            {
                // A genuine fetch error (non-2xx / timeout): distinct from NoTranscript.
                video.Status = VideoJobStatus.Failed;
                video.ErrorMessage = ex.Message;
                await db.SaveChangesAsync(ct);

                log.LogWarning(ex, "Apify fetch failed for video {VideoId} (corr {Corr})", videoId, req.CorrelationId);
                return new IngestTranscriptResult(video.Id, null, VideoJobStatus.Failed);
            }
        }

        /// <summary>
        /// Stamps best-effort Apify metadata onto the video. A null/empty parse leaves the row untouched
        /// (<see cref="MetadataSource.None"/>) so a later backfill or YouTube enrichment can still fill it. When the row
        /// already carries discovery-time YouTube metadata, the fresh Apify parse is merged with it (YouTube keeps the
        /// canonical stats; Apify fills gaps) rather than overwritten.
        /// </summary>
        private static void ApplyMetadata(Video video, VideoMetadata? freshApify)
        {
            if (freshApify is not { HasAnyValue: true }) return; // nothing new; leave any discovery metadata intact

            var existing = VideoMetadata.FromVideo(video);
            var resolved = existing is { Source: MetadataSource.YouTube or MetadataSource.Merged }
                ? VideoMetadataMerge.Combine(freshApify, existing)
                : freshApify;

            resolved!.ApplyTo(video, DateTime.UtcNow);
        }
    }
}
