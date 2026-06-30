using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SeedForge.Domain;
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
            { "snippet": { "title": "First Video" },  "contentDetails": { "videoId": "aaaaaaaaaaa", "videoPublishedAt": "2026-06-28T10:00:00Z" } },
            { "snippet": { "title": "Second Video" }, "contentDetails": { "videoId": "bbbbbbbbbbb", "videoPublishedAt": "2026-06-27T10:00:00Z" } },
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
        public async Task ListRecentUploads_returns_ids_in_order_with_titles()
        {
            var handler = new RoutingHttpMessageHandler().When("playlistItems?", PlaylistItemsJson());
            var client = Build(handler);

            var uploads = await client.ListRecentUploadsAsync(UploadsId);

            Assert.Equal(new[] { "aaaaaaaaaaa", "bbbbbbbbbbb", "ccccccccccc" }, uploads.Select(u => u.VideoId));
            Assert.Equal(new[] { "First Video", "Second Video", null }, uploads.Select(u => u.Title));
            Assert.Contains("part=snippet", handler.LastRequestUri!.ToString()); // title fetched in the same call
        }

        [Fact]
        public async Task Non_2xx_throws_YouTubeException_with_status()
        {
            var handler = new RoutingHttpMessageHandler().When("channels?", """{"error":"forbidden"}""", HttpStatusCode.Forbidden);
            var client = Build(handler);

            var ex = await Assert.ThrowsAsync<YouTubeException>(() => client.ResolveChannelAsync(ChannelId));
            Assert.Equal(403, ex.StatusCode);
        }

        private static string VideosJson() => """
        {
          "items": [
            {
              "id": "aaaaaaaaaaa",
              "snippet": {
                "title": "First",
                "publishedAt": "2026-06-28T10:00:00Z",
                "channelId": "UC_chan_1",
                "description": "first desc",
                "thumbnails": {
                  "default": { "url": "https://img/default.jpg" },
                  "high":    { "url": "https://img/high.jpg" },
                  "maxres":  { "url": "https://img/maxres.jpg" }
                }
              },
              "contentDetails": { "duration": "PT15M33S" },
              "statistics": { "viewCount": "3000000000", "likeCount": "8901", "commentCount": "42" }
            },
            {
              "id": "bbbbbbbbbbb",
              "snippet": { "title": "Second", "publishedAt": "2026-06-27T10:00:00Z", "channelId": "UC_chan_2" },
              "contentDetails": { "duration": "PT1H2M3S" },
              "statistics": { "viewCount": "10" }
            }
          ]
        }
        """;

        [Fact]
        public async Task GetVideoMetadata_maps_duration_stats_and_picks_best_thumbnail()
        {
            var handler = new RoutingHttpMessageHandler().When("videos?", VideosJson());
            var client = Build(handler);

            var map = await client.GetVideoMetadataAsync(new[] { "aaaaaaaaaaa", "bbbbbbbbbbb" });

            Assert.Equal(2, map.Count);

            var a = map["aaaaaaaaaaa"];
            Assert.Equal(933, a.DurationSeconds);                 // PT15M33S ⇒ 933
            Assert.Equal(3_000_000_000L, a.ViewCount);            // string ⇒ long, > int range
            Assert.Equal(8901L, a.LikeCount);
            Assert.Equal(42L, a.CommentCount);
            Assert.Equal(new DateTime(2026, 6, 28, 10, 0, 0, DateTimeKind.Utc), a.PublishedAtUtc);
            Assert.Equal("UC_chan_1", a.YouTubeChannelId);
            Assert.Equal("https://img/maxres.jpg", a.ThumbnailUrl); // highest resolution wins
            Assert.Equal(MetadataSource.YouTube, a.Source);

            var b = map["bbbbbbbbbbb"];
            Assert.Equal(3723, b.DurationSeconds);                // PT1H2M3S ⇒ 3723
            Assert.Equal(10L, b.ViewCount);
            Assert.Null(b.LikeCount);                             // absent ⇒ null, not 0
            Assert.Null(b.ThumbnailUrl);                          // no thumbnails object ⇒ null
        }

        [Fact]
        public async Task GetVideoMetadata_batches_ids_into_one_call_and_keys_by_id()
        {
            var handler = new RoutingHttpMessageHandler().When("videos?", VideosJson());
            var client = Build(handler);

            await client.GetVideoMetadataAsync(new[] { "aaaaaaaaaaa", "bbbbbbbbbbb" });

            Assert.Equal(1, handler.CallCount); // both ids in a single batched videos.list call
            Assert.Contains("id=aaaaaaaaaaa,bbbbbbbbbbb", handler.LastRequestUri!.ToString());
            Assert.Contains("part=snippet,contentDetails,statistics", handler.LastRequestUri!.ToString());
        }

        [Fact]
        public async Task GetVideoMetadata_empty_input_makes_no_call()
        {
            var handler = new RoutingHttpMessageHandler().When("videos?", VideosJson());
            var client = Build(handler);

            var map = await client.GetVideoMetadataAsync(Array.Empty<string>());

            Assert.Empty(map);
            Assert.Equal(0, handler.CallCount);
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
