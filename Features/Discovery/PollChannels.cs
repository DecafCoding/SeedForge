using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SeedForge.Data;
using SeedForge.Domain;
using SeedForge.Services.Queues;
using SeedForge.Services.YouTube;

namespace SeedForge.Features.Discovery
{
    /// <summary>Poll one channel (by row id) or, when null, the whole library.</summary>
    public sealed record PollChannelsRequest(int? ChannelId = null);

    /// <summary>The new uploads enqueued for a single channel during a poll.</summary>
    public sealed record ChannelPollSummary(int ChannelId, string Title, int NewVideoCount);

    /// <summary>The outcome of a poll: a per-channel summary of how many genuinely new uploads were enqueued.</summary>
    public sealed record PollChannelsResult(IReadOnlyList<ChannelPollSummary> Channels);

    /// <summary>
    /// Discovery slice: for each target channel, list recent uploads from its uploads playlist, dedupe against existing
    /// <see cref="Domain.Video"/> rows, enqueue only the genuinely new ids to the <see cref="VideoQueue"/>, and stamp
    /// <c>LastPolledUtc</c>. Dedupe by id is the correctness guarantee; <c>LastPolledUtc</c> is only an optimization.
    /// No transcript or AI work (PRD §2) — discovery only resolves + enqueues. A single channel's failure is caught and
    /// logged so one bad channel doesn't starve the rest. Scoped — uses the scoped <see cref="ApplicationDbContext"/>.
    /// </summary>
    public sealed class PollChannelsHandler(
        ApplicationDbContext db,
        IYouTubeDataClient youtube,
        VideoQueue videoQueue,
        IOptions<YouTubeOptions> youTubeOptions,
        ILogger<PollChannelsHandler> log)
    {
        private readonly YouTubeOptions _ytOpts = youTubeOptions.Value;

        public async Task<PollChannelsResult> HandleAsync(PollChannelsRequest req, CancellationToken ct)
        {
            var channels = req.ChannelId is { } id
                ? await db.Channels.Where(c => c.Id == id).ToListAsync(ct)
                : await db.Channels.ToListAsync(ct);

            var summaries = new List<ChannelPollSummary>(channels.Count);

            foreach (var channel in channels)
            {
                try
                {
                    var recent = await youtube.ListRecentUploadsAsync(channel.UploadsPlaylistId, ct); // newest first
                    var recentIds = recent.Select(u => u.VideoId).ToList();

                    // Dedupe against known videos: only ids with no existing Video row are genuinely new.
                    var known = await db.Videos
                        .Where(v => recentIds.Contains(v.YouTubeVideoId))
                        .Select(v => v.YouTubeVideoId)
                        .ToHashSetAsync(ct);
                    var fresh = recent.Where(u => !known.Contains(u.VideoId)).ToList();

                    // Duration is only known from the videos.list enrichment, and it must be known BEFORE we enqueue so a
                    // short upload never reaches the (paid) transcript step. Prefetch it here; a failure leaves durations
                    // unknown, in which case nothing is filtered (fail-open — same as enrichment being off).
                    var metadata = await FetchMetadataAsync(fresh, ct);

                    var now = DateTime.UtcNow;
                    var newCount = 0;
                    var skippedShort = 0;
                    foreach (var upload in fresh)
                    {
                        metadata.TryGetValue(upload.VideoId, out var meta);

                        if (meta?.DurationSeconds is int seconds && seconds < _ytOpts.MinVideoDurationSeconds)
                        {
                            // Under the minimum: record a terminal SkippedShort marker so it is never re-discovered,
                            // queued, or transcribed. It carries its metadata so the reason is inspectable.
                            var shortVideo = new Video
                            {
                                YouTubeVideoId = upload.VideoId,
                                Url = YouTubeUrl.WatchUrl(upload.VideoId),
                                Title = string.IsNullOrWhiteSpace(upload.Title) ? null : upload.Title,
                                Status = VideoJobStatus.SkippedShort,
                                CreatedAtUtc = now,
                            };
                            meta.ApplyTo(shortVideo, now);
                            db.Videos.Add(shortVideo);
                            skippedShort++;
                            continue;
                        }

                        var rowId = await videoQueue.EnqueueAsync(upload.VideoId, upload.Title, ct); // idempotent on YouTubeVideoId
                        if (meta is not null)
                        {
                            var video = await db.Videos.FirstOrDefaultAsync(v => v.Id == rowId, ct);
                            if (video is not null) meta.ApplyTo(video, now);
                        }
                        newCount++;
                    }

                    channel.LastPolledUtc = now;
                    await db.SaveChangesAsync(ct);

                    log.LogInformation("Polled channel {ChannelId} ({Title}): {New} new, {Short} skipped-short of {Total} recent",
                        channel.Id, channel.Title, newCount, skippedShort, recent.Count);
                    summaries.Add(new ChannelPollSummary(channel.Id, channel.Title, newCount));
                }
                catch (Exception ex)
                {
                    // One channel's resolution/list failure must not abort the whole poll.
                    log.LogError(ex, "Polling channel {ChannelId} ({Title}) failed; continuing", channel.Id, channel.Title);
                    summaries.Add(new ChannelPollSummary(channel.Id, channel.Title, 0));
                }
            }

            return new PollChannelsResult(summaries);
        }

        /// <summary>
        /// Optional, quota-gated duration/metadata prefetch for the fresh uploads: one batched <c>videos.list</c> call
        /// (YouTube-only at discovery — there is no transcript yet). Returns an empty map when enrichment is off, there
        /// is nothing to fetch, or the call fails — callers then treat every duration as unknown (nothing filtered).
        /// </summary>
        private async Task<IReadOnlyDictionary<string, VideoMetadata>> FetchMetadataAsync(
            IReadOnlyList<RecentUpload> fresh, CancellationToken ct)
        {
            if (!_ytOpts.FetchVideoMetadata || fresh.Count == 0)
            {
                return EmptyMetadata;
            }

            try
            {
                return await youtube.GetVideoMetadataAsync(fresh.Select(u => u.VideoId), ct);
            }
            catch (YouTubeException ex)
            {
                log.LogWarning(ex, "Video-metadata prefetch failed during poll; durations unknown, no short-filtering applied");
                return EmptyMetadata;
            }
        }

        private static readonly IReadOnlyDictionary<string, VideoMetadata> EmptyMetadata =
            new Dictionary<string, VideoMetadata>();
    }
}
