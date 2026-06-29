using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SeedForge.Data;
using SeedForge.Domain;
using SeedForge.Services.YouTube;
using SeedForge.Workers;

namespace SeedForge.Services.Queues
{
    /// <summary>
    /// Durable video-job queue: a thin scoped service over <see cref="Video"/> rows, whose <see cref="Video.Status"/>
    /// is the queue state. Claiming atomically flips Pending→InProgress in a transaction (so a UI "process now" can't
    /// double-claim against the worker); failure increments <see cref="Video.AttemptCount"/> and schedules an
    /// exponential <see cref="Video.NextAttemptUtc"/> backoff until it caps to terminal <c>Failed</c>.
    /// </summary>
    public sealed class VideoQueue(
        ApplicationDbContext db,
        IOptions<WorkerOptions> options,
        WorkerControl control,
        ILogger<VideoQueue> log)
    {
        private readonly WorkerOptions _opts = options.Value;

        /// <summary>
        /// Enqueues a YouTube URL/id as a Pending <see cref="Video"/>. Idempotent on <see cref="Video.YouTubeVideoId"/>:
        /// an already-known video is re-armed (Pending, attempts reset) rather than duplicated. Returns the video id.
        /// </summary>
        public async Task<int> EnqueueAsync(string urlOrId, CancellationToken ct = default)
        {
            if (!YouTubeUrl.TryGetVideoId(urlOrId, out var videoId))
            {
                throw new ArgumentException($"'{urlOrId}' is not a recognizable YouTube URL or video id.", nameof(urlOrId));
            }

            var existing = await db.Videos.FirstOrDefaultAsync(v => v.YouTubeVideoId == videoId, ct);
            if (existing is not null)
            {
                // Re-arm a known video for another processing pass without creating a duplicate row.
                existing.Status = VideoJobStatus.Pending;
                existing.AttemptCount = 0;
                existing.NextAttemptUtc = null;
                existing.ErrorMessage = null;
                await db.SaveChangesAsync(ct);
                return existing.Id;
            }

            var video = new Video
            {
                YouTubeVideoId = videoId,
                Url = YouTubeUrl.WatchUrl(videoId),
                Status = VideoJobStatus.Pending,
                CreatedAtUtc = DateTime.UtcNow,
            };
            db.Videos.Add(video);
            await db.SaveChangesAsync(ct);
            log.LogInformation("Enqueued video {VideoId} ({YouTubeId})", video.Id, videoId);
            return video.Id;
        }

        /// <summary>
        /// Atomically claims the next eligible Pending video (highest priority, then oldest), flipping it InProgress
        /// in a short transaction. Returns null when nothing is due. Backoff/priority are respected.
        /// </summary>
        public async Task<Video?> ClaimNextAsync(CancellationToken ct = default)
        {
            var now = DateTime.UtcNow;
            using var tx = await db.Database.BeginTransactionAsync(ct);

            var job = await db.Videos
                .Where(v => v.Status == VideoJobStatus.Pending
                         && (v.NextAttemptUtc == null || v.NextAttemptUtc <= now))
                .OrderByDescending(v => v.Priority).ThenBy(v => v.Id)
                .FirstOrDefaultAsync(ct);

            if (job is null)
            {
                await tx.CommitAsync(ct);
                return null;
            }

            job.Status = VideoJobStatus.InProgress;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return job;
        }

        /// <summary>Marks a claimed video with its terminal/outcome status (Done / NoTranscript / ProcessedNoIdeas / Failed).</summary>
        public async Task CompleteAsync(int videoId, VideoJobStatus status, CancellationToken ct = default)
        {
            var job = await db.Videos.FirstOrDefaultAsync(v => v.Id == videoId, ct)
                ?? throw new InvalidOperationException($"Video {videoId} not found");
            job.Status = status;
            await db.SaveChangesAsync(ct);
        }

        /// <summary>
        /// Records a failed attempt: increments the attempt count and either reschedules with exponential backoff
        /// or, once <see cref="WorkerOptions.MaxAttempts"/> is reached, marks the job terminal <c>Failed</c>.
        /// </summary>
        public async Task FailAsync(int videoId, string? error = null, CancellationToken ct = default)
        {
            var job = await db.Videos.FirstOrDefaultAsync(v => v.Id == videoId, ct)
                ?? throw new InvalidOperationException($"Video {videoId} not found");

            job.AttemptCount++;
            job.ErrorMessage = error;
            if (job.AttemptCount >= _opts.MaxAttempts)
            {
                job.Status = VideoJobStatus.Failed;
                job.NextAttemptUtc = null;
            }
            else
            {
                job.Status = VideoJobStatus.Pending;
                job.NextAttemptUtc = DateTime.UtcNow.AddSeconds(_opts.BackoffBaseSeconds * Math.Pow(2, job.AttemptCount));
            }
            await db.SaveChangesAsync(ct);
            log.LogWarning("Video {VideoId} failed (attempt {Attempt}/{Max}); status {Status}",
                videoId, job.AttemptCount, _opts.MaxAttempts, job.Status);
        }

        /// <summary>Pending count for the queue view (drives the drain-time ETA).</summary>
        public Task<int> CountPendingAsync(CancellationToken ct = default) =>
            db.Videos.CountAsync(v => v.Status == VideoJobStatus.Pending, ct);

        /// <summary>
        /// Jumps a video to the front of the queue: raises its priority, clears any backoff, and wakes the Processing
        /// worker via <see cref="WorkerControl"/> so it runs on the next tick rather than after the full interval.
        /// </summary>
        public async Task ProcessNowAsync(int videoId, CancellationToken ct = default)
        {
            var job = await db.Videos.FirstOrDefaultAsync(v => v.Id == videoId, ct)
                ?? throw new InvalidOperationException($"Video {videoId} not found");
            job.Priority = Math.Max(job.Priority + 1, 1);
            job.NextAttemptUtc = DateTime.UtcNow;
            if (job.Status != VideoJobStatus.InProgress)
            {
                job.Status = VideoJobStatus.Pending;
            }
            await db.SaveChangesAsync(ct);
            control.Signal(Worker.Processing);
        }
    }
}
