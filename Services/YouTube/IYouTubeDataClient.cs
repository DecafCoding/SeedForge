using SeedForge.Domain;

namespace SeedForge.Services.YouTube
{
    /// <summary>The resolved identity of a channel: its canonical id, display title, and the uploads playlist that lists its videos.</summary>
    public sealed record ResolvedChannel(string ChannelId, string Title, string UploadsPlaylistId);

    /// <summary>
    /// Read-only boundary to the YouTube Data API v3 used by Discovery: resolve a channel reference to its identity
    /// (id + title + uploads playlist), list the channel's most recent video ids, and fetch per-video metadata.
    /// No transcript or AI work.
    /// </summary>
    public interface IYouTubeDataClient
    {
        /// <summary>Resolves an id / <c>/channel/</c> URL / <c>@handle</c> / legacy custom reference to a <see cref="ResolvedChannel"/>.</summary>
        Task<ResolvedChannel> ResolveChannelAsync(string input, CancellationToken ct = default);

        /// <summary>Lists the channel's most recent video ids (newest first) from its uploads playlist.</summary>
        Task<IReadOnlyList<string>> ListRecentVideoIdsAsync(string uploadsPlaylistId, CancellationToken ct = default);

        /// <summary>
        /// Fetches per-video metadata for the given ids via a single batched <c>videos.list</c> call (≤50 ids = 1 quota
        /// unit). Returns a map keyed by YouTube video id; ids with no result (private/removed) are simply absent — the
        /// result is not positionally aligned to the input.
        /// </summary>
        Task<IReadOnlyDictionary<string, VideoMetadata>> GetVideoMetadataAsync(
            IEnumerable<string> videoIds, CancellationToken ct = default);
    }
}
