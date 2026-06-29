using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SeedForge.Services.YouTube;

namespace SeedForge.UnitTests
{
    /// <summary>The typed YouTube client resolves a channel (id + @handle) and lists recent uploads from canned API JSON; non-2xx throws.</summary>
    public class YouTubeDataClientTests
    {
        private const string ChannelId = "UC_x5XG1OV2P6uZZ5FSM9Ttw";
        private const string UploadsId = "UU_x5XG1OV2P6uZZ5FSM9Ttw";

        private static string ChannelsJson() => $$"""
        {
          "items": [
            {
              "id": "{{ChannelId}}",
              "snippet": { "title": "Google Developers" },
              "contentDetails": { "relatedPlaylists": { "uploads": "{{UploadsId}}" } }
            }
          ]
        }
        """;

        private static string PlaylistItemsJson() => """
        {
          "items": [
            { "contentDetails": { "videoId": "aaaaaaaaaaa", "videoPublishedAt": "2026-06-28T10:00:00Z" } },
            { "contentDetails": { "videoId": "bbbbbbbbbbb", "videoPublishedAt": "2026-06-27T10:00:00Z" } },
            { "contentDetails": { "videoId": "ccccccccccc", "videoPublishedAt": "2026-06-26T10:00:00Z" } }
          ]
        }
        """;

        private static YouTubeDataClient Build(HttpMessageHandler handler, string apiKey = "AIzaTEST")
        {
            var http = new HttpClient(handler) { BaseAddress = new Uri("https://www.googleapis.com/youtube/v3/") };
            var options = Options.Create(new YouTubeOptions { ApiKey = apiKey, MaxResults = 20 });
            return new YouTubeDataClient(http, options, NullLogger<YouTubeDataClient>.Instance);
        }

        [Fact]
        public async Task Resolve_by_id_returns_channelId_title_and_uploads_playlist()
        {
            var handler = new RoutingHttpMessageHandler().When("channels?", ChannelsJson());
            var client = Build(handler);

            var resolved = await client.ResolveChannelAsync(ChannelId);

            Assert.Equal(ChannelId, resolved.ChannelId);
            Assert.Equal("Google Developers", resolved.Title);
            Assert.Equal(UploadsId, resolved.UploadsPlaylistId);
            // Key is passed as a query param; the id query was used.
            Assert.Contains("key=AIzaTEST", handler.LastRequestUri!.ToString());
            Assert.Contains($"id={ChannelId}", handler.LastRequestUri!.ToString());
        }

        [Fact]
        public async Task Resolve_by_handle_uses_forHandle_query()
        {
            var handler = new RoutingHttpMessageHandler().When("channels?", ChannelsJson());
            var client = Build(handler);

            var resolved = await client.ResolveChannelAsync("@GoogleDevelopers");

            Assert.Equal(ChannelId, resolved.ChannelId);
            Assert.Contains("forHandle=%40GoogleDevelopers", handler.LastRequestUri!.ToString());
        }

        [Fact]
        public async Task ListRecentVideoIds_returns_ids_in_order()
        {
            var handler = new RoutingHttpMessageHandler().When("playlistItems?", PlaylistItemsJson());
            var client = Build(handler);

            var ids = await client.ListRecentVideoIdsAsync(UploadsId);

            Assert.Equal(new[] { "aaaaaaaaaaa", "bbbbbbbbbbb", "ccccccccccc" }, ids);
        }

        [Fact]
        public async Task Non_2xx_throws_YouTubeException_with_status()
        {
            var handler = new RoutingHttpMessageHandler().When("channels?", """{"error":"forbidden"}""", HttpStatusCode.Forbidden);
            var client = Build(handler);

            var ex = await Assert.ThrowsAsync<YouTubeException>(() => client.ResolveChannelAsync(ChannelId));
            Assert.Equal(403, ex.StatusCode);
        }

        [Fact]
        public async Task Custom_reference_falls_back_to_search_then_channels()
        {
            const string searchJson = $$"""
            { "items": [ { "id": { "channelId": "{{ChannelId}}" }, "snippet": { "title": "Linus Tech Tips" } } ] }
            """;
            var handler = new RoutingHttpMessageHandler()
                .When("search?", searchJson)
                .When("channels?", ChannelsJson());
            var client = Build(handler);

            var resolved = await client.ResolveChannelAsync("https://www.youtube.com/c/LinusTechTips");

            Assert.Equal(ChannelId, resolved.ChannelId);
            Assert.Equal(UploadsId, resolved.UploadsPlaylistId);
            // Two calls: the search fallback, then channels.list by the resolved id.
            Assert.Equal(2, handler.CallCount);
        }
    }
}
