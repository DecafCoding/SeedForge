using Microsoft.Extensions.Logging.Abstractions;
using SeedForge.Features.Discovery;
using SeedForge.Services.YouTube;
using SeedForge.UnitTests.Fakes;

namespace SeedForge.UnitTests
{
    /// <summary>The channel library resolves + persists a deduped Channel and lists/removes rows.</summary>
    public class ChannelLibraryTests : IDisposable
    {
        private readonly SliceTestHarness _h = new();
        private const string ChannelId = "UC_x5XG1OV2P6uZZ5FSM9Ttw";
        private const string UploadsId = "UU_x5XG1OV2P6uZZ5FSM9Ttw";

        private ChannelLibrary NewLibrary(FakeYouTubeDataClient yt) =>
            new(_h.NewDb(), yt, NullLogger<ChannelLibrary>.Instance);

        [Fact]
        public async Task AddAsync_persists_channel_with_resolved_id_and_uploads_playlist()
        {
            var yt = new FakeYouTubeDataClient()
                .Resolves("@GoogleDevelopers", new ResolvedChannel(ChannelId, "Google Developers", UploadsId));

            var channel = await NewLibrary(yt).AddAsync("@GoogleDevelopers");

            Assert.Equal(ChannelId, channel.YouTubeChannelId);
            Assert.Equal("Google Developers", channel.Title);
            Assert.Equal(UploadsId, channel.UploadsPlaylistId);

            using var read = _h.NewDb();
            var stored = read.Channels.Single();
            Assert.Equal(ChannelId, stored.YouTubeChannelId);
            Assert.Equal(UploadsId, stored.UploadsPlaylistId);
        }

        [Fact]
        public async Task AddAsync_is_idempotent_on_channel_id()
        {
            var yt = new FakeYouTubeDataClient()
                .Resolves("@GoogleDevelopers", new ResolvedChannel(ChannelId, "Google Developers", UploadsId))
                .Resolves($"https://www.youtube.com/channel/{ChannelId}", new ResolvedChannel(ChannelId, "Google Developers", UploadsId));

            await NewLibrary(yt).AddAsync("@GoogleDevelopers");
            await NewLibrary(yt).AddAsync($"https://www.youtube.com/channel/{ChannelId}");

            using var read = _h.NewDb();
            Assert.Single(read.Channels);
        }

        [Fact]
        public async Task AddAsync_surfaces_a_clear_error_for_an_unresolvable_input()
        {
            var yt = new FakeYouTubeDataClient(); // nothing registered ⇒ resolution throws
            await Assert.ThrowsAsync<YouTubeException>(() => NewLibrary(yt).AddAsync("https://www.youtube.com/c/Nope"));

            using var read = _h.NewDb();
            Assert.Empty(read.Channels);
        }

        [Fact]
        public async Task RemoveAsync_deletes_the_row()
        {
            var yt = new FakeYouTubeDataClient()
                .Resolves("@GoogleDevelopers", new ResolvedChannel(ChannelId, "Google Developers", UploadsId));
            var channel = await NewLibrary(yt).AddAsync("@GoogleDevelopers");

            await NewLibrary(yt).RemoveAsync(channel.Id);

            using var read = _h.NewDb();
            Assert.Empty(read.Channels);
        }

        public void Dispose() => _h.Dispose();
    }
}
