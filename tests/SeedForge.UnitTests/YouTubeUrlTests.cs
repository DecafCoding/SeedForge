using SeedForge.Services.YouTube;

namespace SeedForge.UnitTests
{
    /// <summary>Proves the URL helper normalizes every common YouTube form (and a bare id) to the 11-char id and rejects junk.</summary>
    public class YouTubeUrlTests
    {
        [Theory]
        [InlineData("https://www.youtube.com/watch?v=abc12345678&t=2s")]
        [InlineData("https://youtube.com/watch?v=abc12345678")]
        [InlineData("https://youtu.be/abc12345678")]
        [InlineData("https://youtu.be/abc12345678?si=xyz")]
        [InlineData("https://www.youtube.com/shorts/abc12345678")]
        [InlineData("https://www.youtube.com/embed/abc12345678")]
        [InlineData("abc12345678")]
        public void TryGetVideoId_extracts_id_from_all_forms(string input)
        {
            Assert.True(YouTubeUrl.TryGetVideoId(input, out var id));
            Assert.Equal("abc12345678", id);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("not a url")]
        [InlineData("https://example.com/watch?v=abc12345678")]
        [InlineData("https://www.youtube.com/watch?v=tooshort")]
        [InlineData("https://www.youtube.com/watch?list=PL123")]
        public void TryGetVideoId_rejects_junk(string input)
        {
            Assert.False(YouTubeUrl.TryGetVideoId(input, out var id));
            Assert.Equal(string.Empty, id);
        }

        [Fact]
        public void WatchUrl_builds_canonical_watch_url()
        {
            Assert.Equal("https://www.youtube.com/watch?v=abc12345678", YouTubeUrl.WatchUrl("abc12345678"));
        }
    }
}
