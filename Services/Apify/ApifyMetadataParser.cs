using System.Globalization;
using System.Text.Json;
using SeedForge.Domain;

namespace SeedForge.Services.Apify
{
    /// <summary>
    /// Pure, defensive parser that lifts per-video <see cref="VideoMetadata"/> out of the verbatim Apify dataset item
    /// (<c>streamers~youtube-scraper</c>) already stored in <c>Transcript.RawDatasetItemJson</c> — at zero extra API
    /// cost. Every probe tolerates a missing key, a wrong type, or a drifted field name (multiple keys tried in order);
    /// a malformed document yields <c>null</c> rather than throwing. Counts come back as <see cref="long"/> and a
    /// hidden/absent value stays null (never coerced to zero).
    /// </summary>
    public static class ApifyMetadataParser
    {
        /// <summary>Parses the raw item JSON. Returns null when the input is blank or not parseable JSON.</summary>
        public static VideoMetadata? TryParse(string? rawJson)
        {
            if (string.IsNullOrWhiteSpace(rawJson)) return null;

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(rawJson);
            }
            catch (JsonException)
            {
                return null;
            }

            using (doc)
            {
                var r = doc.RootElement;
                if (r.ValueKind != JsonValueKind.Object) return null;

                return new VideoMetadata(
                    DurationSeconds: ReadDuration(r, "duration", "lengthSeconds", "durationSeconds", "length"),
                    ViewCount: ReadLong(r, "viewCount", "views"),
                    LikeCount: ReadLong(r, "likes", "likeCount"),
                    CommentCount: ReadLong(r, "commentsCount", "commentCount", "comments"),
                    PublishedAtUtc: ReadDate(r, "date", "publishDate", "uploadDate", "publishedAt"),
                    Description: ReadString(r, "text", "description"),
                    ThumbnailUrl: ReadThumbnail(r, "thumbnailUrl", "thumbnail", "thumbnails"),
                    YouTubeChannelId: ReadString(r, "channelId", "channelIdRaw"),
                    Source: MetadataSource.Apify);
            }
        }

        /// <summary>Duration from a numeric seconds value, a clock string ("h:mm:ss" / "mm:ss"), or a plain numeric string.</summary>
        private static int? ReadDuration(JsonElement r, params string[] keys)
        {
            foreach (var key in keys)
            {
                if (!r.TryGetProperty(key, out var el)) continue;

                if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n)) return n;

                if (el.ValueKind == JsonValueKind.String)
                {
                    var s = el.GetString();
                    if (string.IsNullOrWhiteSpace(s)) continue;
                    s = s.Trim();

                    if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var plain)) return plain;

                    var clock = ParseClock(s);
                    if (clock is not null) return clock;
                }
            }
            return null;
        }

        /// <summary>"1:02:03" → 3723, "15:33" → 933. Returns null when not a clock-shaped value.</summary>
        private static int? ParseClock(string s)
        {
            var parts = s.Split(':');
            if (parts.Length is < 2 or > 3) return null;

            var total = 0;
            foreach (var part in parts)
            {
                if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) || v < 0)
                    return null;
                total = total * 60 + v;
            }
            return total;
        }

        /// <summary>A count from a numeric value or a numeric string. Null on absence/wrong type — never coerced to 0.</summary>
        private static long? ReadLong(JsonElement r, params string[] keys)
        {
            foreach (var key in keys)
            {
                if (!r.TryGetProperty(key, out var el)) continue;

                if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var n)) return n;

                if (el.ValueKind == JsonValueKind.String)
                {
                    var s = el.GetString();
                    if (!string.IsNullOrWhiteSpace(s)
                        && long.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                    {
                        return parsed;
                    }
                }
            }
            return null;
        }

        /// <summary>A UTC date from an ISO-8601 / RFC-3339 string (or a "yyyy-MM-dd" upload date).</summary>
        private static DateTime? ReadDate(JsonElement r, params string[] keys)
        {
            foreach (var key in keys)
            {
                if (!r.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.String) continue;

                var s = el.GetString();
                if (string.IsNullOrWhiteSpace(s)) continue;

                if (DateTime.TryParse(s, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
                {
                    return dt;
                }
            }
            return null;
        }

        /// <summary>First non-empty string among the probed keys.</summary>
        private static string? ReadString(JsonElement r, params string[] keys)
        {
            foreach (var key in keys)
            {
                if (r.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.String)
                {
                    var s = el.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) return s;
                }
            }
            return null;
        }

        /// <summary>
        /// A thumbnail URL: a plain string key, or the highest-resolution entry of a thumbnails array of
        /// <c>{ url, width, height }</c> objects (or string elements).
        /// </summary>
        private static string? ReadThumbnail(JsonElement r, params string[] keys)
        {
            foreach (var key in keys)
            {
                if (!r.TryGetProperty(key, out var el)) continue;

                if (el.ValueKind == JsonValueKind.String)
                {
                    var s = el.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) return s;
                }

                if (el.ValueKind == JsonValueKind.Array)
                {
                    string? best = null;
                    var bestWidth = -1;
                    foreach (var item in el.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String)
                        {
                            best = item.GetString() ?? best; // last string wins as a coarse "highest res"
                            continue;
                        }
                        if (item.ValueKind != JsonValueKind.Object) continue;

                        var url = item.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String
                            ? u.GetString() : null;
                        if (string.IsNullOrWhiteSpace(url)) continue;

                        var width = item.TryGetProperty("width", out var w) && w.ValueKind == JsonValueKind.Number
                            && w.TryGetInt32(out var iw) ? iw : 0;
                        if (width >= bestWidth)
                        {
                            bestWidth = width;
                            best = url;
                        }
                    }
                    if (!string.IsNullOrWhiteSpace(best)) return best;
                }
            }
            return null;
        }
    }
}
