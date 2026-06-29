using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SeedForge.Services.Apify;

namespace SeedForge.UnitTests
{
    /// <summary>Proves the ingestion service parses the first dataset item defensively and reports HadTranscript without ever throwing on a drifted/empty shape.</summary>
    public class ApifyIngestionServiceTests
    {
        private static ApifyIngestionService Build(string datasetJson)
        {
            var handler = new StubHttpMessageHandler(datasetJson);
            var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.apify.com") };
            var client = new ApifyClient(http, Options.Create(new ApifyOptions { Token = "t" }), NullLogger<ApifyClient>.Instance);
            return new ApifyIngestionService(client, NullLogger<ApifyIngestionService>.Instance);
        }

        [Fact]
        public async Task Subtitles_array_yields_transcript_text_title_channel_and_raw_item()
        {
            const string dataset = """
            [
              {
                "title": "How to Terraform Mars",
                "channelName": "SciFi Lab",
                "subtitles": [ { "plaintext": "Line one." }, { "plaintext": "Line two." } ]
              }
            ]
            """;

            var ingested = await Build(dataset).FetchAsync("https://www.youtube.com/watch?v=abc12345678");

            Assert.True(ingested.HadTranscript);
            Assert.Contains("Line one.", ingested.Text);
            Assert.Contains("Line two.", ingested.Text);
            Assert.Equal("How to Terraform Mars", ingested.Title);
            Assert.Equal("SciFi Lab", ingested.ChannelName);
            Assert.False(string.IsNullOrWhiteSpace(ingested.RawItemJson));
            Assert.Equal("abc12345678", ingested.YouTubeVideoId);
        }

        [Fact]
        public async Task Transcript_key_is_probed_when_no_subtitles()
        {
            const string dataset = """[ { "title": "T", "transcript": "Just plain transcript text." } ]""";

            var ingested = await Build(dataset).FetchAsync("abc12345678");

            Assert.True(ingested.HadTranscript);
            Assert.Equal("Just plain transcript text.", ingested.Text);
        }

        [Fact]
        public async Task Item_with_no_caption_keys_reports_no_transcript_without_throwing()
        {
            const string dataset = """[ { "title": "Captionless", "channelName": "Ch" } ]""";

            var ingested = await Build(dataset).FetchAsync("abc12345678");

            Assert.False(ingested.HadTranscript);
            Assert.Null(ingested.Text);
            Assert.Equal("Captionless", ingested.Title);
        }

        [Fact]
        public async Task Empty_dataset_array_reports_no_transcript_without_throwing()
        {
            var ingested = await Build("[]").FetchAsync("abc12345678");

            Assert.False(ingested.HadTranscript);
            Assert.Null(ingested.Text);
            Assert.Equal(string.Empty, ingested.RawItemJson);
        }

        [Fact]
        public async Task Invalid_url_throws_ApifyException_before_any_call()
        {
            await Assert.ThrowsAsync<ApifyException>(() => Build("[]").FetchAsync("not a youtube url"));
        }
    }
}
