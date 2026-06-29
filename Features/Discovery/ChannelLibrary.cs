using Microsoft.EntityFrameworkCore;
using SeedForge.Data;
using SeedForge.Domain;
using SeedForge.Services.YouTube;

namespace SeedForge.Features.Discovery
{
    /// <summary>
    /// Maintains the deduped library of followed YouTube channels. <see cref="AddAsync"/> resolves a channel reference
    /// (id / <c>/channel/</c> URL / <c>@handle</c> / legacy custom) to its identity via <see cref="IYouTubeDataClient"/>
    /// and persists a <see cref="Channel"/> — idempotent on the resolved channel id (a unique index backs the dedupe).
    /// The uploads playlist id is stored at add-time so each poll is a single playlistItems.list call. Scoped: it uses
    /// the scoped <see cref="ApplicationDbContext"/>.
    /// </summary>
    public sealed class ChannelLibrary(
        ApplicationDbContext db, IYouTubeDataClient youtube, ILogger<ChannelLibrary> log)
    {
        /// <summary>
        /// Resolves <paramref name="input"/> and adds it to the library, or returns the existing row if the channel is
        /// already followed. A resolution failure surfaces as a <see cref="YouTubeException"/> (or <see cref="ArgumentException"/>
        /// for unparseable input), never a duplicate row.
        /// </summary>
        public async Task<Channel> AddAsync(string input, CancellationToken ct = default)
        {
            var resolved = await youtube.ResolveChannelAsync(input, ct);

            // Idempotent on the resolved channel id (uniquely indexed): never write a second row for one channel.
            var existing = await db.Channels.FirstOrDefaultAsync(c => c.YouTubeChannelId == resolved.ChannelId, ct);
            if (existing is not null)
            {
                log.LogInformation("Channel {ChannelId} already followed; reusing row {Id}", resolved.ChannelId, existing.Id);
                return existing;
            }

            var channel = new Channel
            {
                YouTubeChannelId = resolved.ChannelId,
                Title = resolved.Title,
                UploadsPlaylistId = resolved.UploadsPlaylistId,
                AddedAtUtc = DateTime.UtcNow,
            };
            db.Channels.Add(channel);
            await db.SaveChangesAsync(ct);
            log.LogInformation("Added channel {ChannelId} ({Title}) to the library", resolved.ChannelId, resolved.Title);
            return channel;
        }

        /// <summary>All followed channels, newest first.</summary>
        public Task<List<Channel>> ListAsync(CancellationToken ct = default) =>
            db.Channels.OrderByDescending(c => c.AddedAtUtc).ToListAsync(ct);

        /// <summary>Removes a channel from the library by row id. No-op if it's already gone.</summary>
        public async Task RemoveAsync(int id, CancellationToken ct = default)
        {
            var channel = await db.Channels.FirstOrDefaultAsync(c => c.Id == id, ct);
            if (channel is null) return;
            db.Channels.Remove(channel);
            await db.SaveChangesAsync(ct);
            log.LogInformation("Removed channel {Id} ({ChannelId}) from the library", id, channel.YouTubeChannelId);
        }
    }
}
