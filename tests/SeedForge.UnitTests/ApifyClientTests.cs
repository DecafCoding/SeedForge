using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SeedForge.Services.Apify;

namespace SeedForge.UnitTests
{
    /// <summary>Proves the typed Apify client posts to the right URL with the token + input and throws <see cref="ApifyException"/> on non-2xx.</summary>
    public class ApifyClientTests
    {
        private static ApifyClient Build(StubHttpMessageHandler handler, string token = "apify_api_test")
        {
            var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.apify.com") };
            var options = Options.Create(new ApifyOptions { Token = token });
            return new ApifyClient(http, options, NullLogger<ApifyClient>.Instance);
        }

        [Fact]
        public async Task RunSync_posts_to_actor_endpoint_with_token_and_input_body()
        {
            var handler = new StubHttpMessageHandler("[]");
            var client = Build(handler);

            await client.RunSyncGetDatasetItemsAsync(new { startUrls = new[] { new { url = "https://www.youtube.com/watch?v=abc12345678" } } });

            var uri = handler.LastRequestUri!.ToString();
            Assert.Contains("acts/streamers~youtube-scraper/run-sync-get-dataset-items", uri);
            Assert.Contains("token=apify_api_test", uri);

            // The actor input is sent as the JSON request body.
            var body = JsonNode.Parse(handler.LastRequestBody!)!.AsObject();
            Assert.Equal("https://www.youtube.com/watch?v=abc12345678",
                body["startUrls"]![0]!["url"]!.GetValue<string>());
        }

        [Fact]
        public async Task RunSync_returns_parsed_array_root_and_null_cost_when_no_header()
        {
            var handler = new StubHttpMessageHandler("""[{"title":"hi"}]""");
            var client = Build(handler);

            var result = await client.RunSyncGetDatasetItemsAsync(new { });

            Assert.Equal(System.Text.Json.JsonValueKind.Array, result.Items.ValueKind);
            Assert.Equal(1, result.Items.GetArrayLength());
            Assert.Null(result.CostUnits);
        }

        [Fact]
        public async Task Non_2xx_throws_ApifyException_with_status()
        {
            var handler = new StubHttpMessageHandler("""{"error":"boom"}""", HttpStatusCode.InternalServerError);
            var client = Build(handler);

            var ex = await Assert.ThrowsAsync<ApifyException>(() => client.RunSyncGetDatasetItemsAsync(new { }));
            Assert.Equal(500, ex.StatusCode);
        }
    }
}
