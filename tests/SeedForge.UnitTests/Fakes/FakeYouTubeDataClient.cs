using SeedForge.Domain;
using SeedForge.Services.YouTube;

namespace SeedForge.UnitTests.Fakes
{
    /// <summary>
    /// In-memory <see cref="IYouTubeDataClient"/> for discovery tests: maps a channel input to a canned
    /// <see cref="ResolvedChannel"/> and an uploads-playlist id to a canned recent-id list. A registered throw lets a
    /// test simulate a single channel's API failure during a library poll.
    /// </summary>
    public sealed class FakeYouTubeDataClient : IYouTubeDataClient
    {
        private readonly Dictionary<string, ResolvedChannel> _resolutions = new();
        private readonly Dictionary<string, IReadOnlyList<RecentUpload>> _recent = new();
        private readonly HashSet<string> _throwOnPlaylist = new();
        private readonly Dictionary<string, VideoMetadata> _metadata = new(StringComparer.Ordinal);

        public int ResolveCalls { get; private set; }
        public int ListCalls { get; private set; }
        public int MetadataCalls { get; private set; }

        public FakeYouTubeDataClient Resolves(string input, ResolvedChannel resolved)
        {
            _resolutions[input] = resolved;
            return this;
        }

        public FakeYouTubeDataClient HasRecent(string uploadsPlaylistId, params string[] videoIds)
        {
            _recent[uploadsPlaylistId] = videoIds.Select(id => new RecentUpload(id, null)).ToList();
            return this;
        }

        public FakeYouTubeDataClient HasRecentUploads(string uploadsPlaylistId, params RecentUpload[] uploads)
        {
            _recent[uploadsPlaylistId] = uploads;
            return this;
        }

        public FakeYouTubeDataClient ThrowsOnList(string uploadsPlaylistId)
        {
            _throwOnPlaylist.Add(uploadsPlaylistId);
            return this;
        }

        public FakeYouTubeDataClient HasMetadata(string videoId, VideoMetadata metadata)
        {
            _metadata[videoId] = metadata;
            return this;
        }

        public Task<ResolvedChannel> ResolveChannelAsync(string input, CancellationToken ct = default)
        {
            ResolveCalls++;
            if (_resolutions.TryGetValue(input, out var resolved)) return Task.FromResult(resolved);
            throw new YouTubeException($"FakeYouTubeDataClient: no resolution registered for '{input}'.");
        }

        public Task<IReadOnlyList<RecentUpload>> ListRecentUploadsAsync(string uploadsPlaylistId, CancellationToken ct = default)
        {
            ListCalls++;
            if (_throwOnPlaylist.Contains(uploadsPlaylistId))
                throw new YouTubeException($"FakeYouTubeDataClient: forced failure for '{uploadsPlaylistId}'.");
            return Task.FromResult(_recent.TryGetValue(uploadsPlaylistId, out var uploads) ? uploads : Array.Empty<RecentUpload>());
        }

        public Task<IReadOnlyDictionary<string, VideoMetadata>> GetVideoMetadataAsync(
            IEnumerable<string> videoIds, CancellationToken ct = default)
        {
            MetadataCalls++;
            IReadOnlyDictionary<string, VideoMetadata> map = videoIds
                .Where(id => _metadata.ContainsKey(id))
                .ToDictionary(id => id, id => _metadata[id], StringComparer.Ordinal);
            return Task.FromResult(map);
        }
    }
}
