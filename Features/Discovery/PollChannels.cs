using Microsoft.EntityFrameworkCore;
using SeedForge.Data;
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
        ILogger<PollChannelsHandler> log)
    {
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
                    var recent = await youtube.ListRecentVideoIdsAsync(channel.UploadsPlaylistId, ct); // newest first

                    // Dedupe against known videos: only ids with no existing Video row are genuinely new.
                    var known = await db.Videos
                        .Where(v => recent.Contains(v.YouTubeVideoId))
                        .Select(v => v.YouTubeVideoId)
                        .ToHashSetAsync(ct);

                    var newCount = 0;
                    foreach (var videoId in recent.Where(videoId => !known.Contains(videoId)))
                    {
                        await videoQueue.EnqueueAsync(videoId, ct); // idempotent on YouTubeVideoId
                        newCount++;
                    }

                    channel.LastPolledUtc = DateTime.UtcNow;
                    await db.SaveChangesAsync(ct);

                    log.LogInformation("Polled channel {ChannelId} ({Title}): {New} new of {Total} recent",
                        channel.Id, channel.Title, newCount, recent.Count);
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
    }
}
