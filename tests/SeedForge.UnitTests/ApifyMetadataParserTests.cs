using SeedForge.Domain;
using SeedForge.Services.Apify;

namespace SeedForge.UnitTests
{
    /// <summary>
    /// The Apify metadata parser lifts duration/counts/date/description/thumbnail/channel out of the raw dataset item
    /// defensively: it tolerates seconds vs clock durations, string vs numeric counts, hidden/absent fields (→ null,
    /// never 0), drifted key names, and malformed JSON (→ null, never throws).
    /// </summary>
    public class ApifyMetadataParserTests
    {
        [Fact]
        public void Parses_full_item_with_numeric_duration_and_counts()
        {
            const string raw = """
            {
              "title": "A video",
              "duration": 933,
              "viewCount": 1234567,
              "likes": 8901,
              "commentsCount": 42,
              "date": "2026-06-28T10:00:00Z",
              "text": "the description",
              "thumbnailUrl": "https://img/hq.jpg",
              "channelId": "UC_channel_123"
            }
            """;

            var m = ApifyMetadataParser.TryParse(raw);

            Assert.NotNull(m);
            Assert.Equal(933, m!.DurationSeconds);
            Assert.Equal(1234567L, m.ViewCount);
            Assert.Equal(8901L, m.LikeCount);
            Assert.Equal(42L, m.CommentCount);
            Assert.Equal(new DateTime(2026, 6, 28, 10, 0, 0, DateTimeKind.Utc), m.PublishedAtUtc);
            Assert.Equal("the description", m.Description);
            Assert.Equal("https://img/hq.jpg", m.ThumbnailUrl);
            Assert.Equal("UC_channel_123", m.YouTubeChannelId);
            Assert.Equal(MetadataSource.Apify, m.Source);
        }

        [Theory]
        [InlineData("1:02:03", 3723)]
        [InlineData("15:33", 933)]
        [InlineData("45", 45)]
        public void Parses_clock_and_plain_string_durations(string value, int expected)
        {
            var m = ApifyMetadataParser.TryParse($$"""{ "duration": "{{value}}" }""");
            Assert.Equal(expected, m!.DurationSeconds);
        }

        [Fact]
        public void View_count_as_string_parses_to_long()
        {
            var m = ApifyMetadataParser.TryParse("""{ "views": "3000000000" }""");
            Assert.Equal(3_000_000_000L, m!.ViewCount); // > int.MaxValue ⇒ proves long is used
        }

        [Fact]
        public void Hidden_or_absent_counts_are_null_not_zero()
        {
            var m = ApifyMetadataParser.TryParse("""{ "title": "no stats here" }""");

            Assert.NotNull(m);
            Assert.Null(m!.ViewCount);
            Assert.Null(m.LikeCount);
            Assert.Null(m.CommentCount);
            Assert.Null(m.DurationSeconds);
            Assert.Equal(MetadataSource.Apify, m.Source);
        }

        [Fact]
        public void Drifted_key_names_are_tolerated()
        {
            // Alternate spellings the actor has used at various times.
            const string raw = """
            { "lengthSeconds": 120, "views": 50, "likeCount": 7, "uploadDate": "2026-01-15", "description": "alt" }
            """;

            var m = ApifyMetadataParser.TryParse(raw);

            Assert.Equal(120, m!.DurationSeconds);
            Assert.Equal(50L, m.ViewCount);
            Assert.Equal(7L, m.LikeCount);
            Assert.Equal(new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc), m.PublishedAtUtc);
            Assert.Equal("alt", m.Description);
        }

        [Fact]
        public void Thumbnail_array_picks_highest_resolution()
        {
            const string raw = """
            {
              "thumbnails": [
                { "url": "https://img/sd.jpg", "width": 320 },
                { "url": "https://img/hd.jpg", "width": 1280 },
                { "url": "https://img/md.jpg", "width": 640 }
              ]
            }
            """;

            var m = ApifyMetadataParser.TryParse(raw);
            Assert.Equal("https://img/hd.jpg", m!.ThumbnailUrl);
        }

        [Fact]
        public void Wrong_typed_fields_do_not_throw_and_yield_nulls()
        {
            // duration as an object, viewCount as bool, date as number — all unusable, none should throw.
            const string raw = """{ "duration": {}, "viewCount": true, "date": 12345 }""";

            var m = ApifyMetadataParser.TryParse(raw);

            Assert.NotNull(m);
            Assert.Null(m!.DurationSeconds);
            Assert.Null(m.ViewCount);
            Assert.Null(m.PublishedAtUtc);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        [InlineData("not json at all {")]
        [InlineData("[1,2,3]")] // valid JSON but not an object
        public void Blank_or_malformed_or_non_object_returns_null(string? raw)
        {
            Assert.Null(ApifyMetadataParser.TryParse(raw));
        }
    }
}
