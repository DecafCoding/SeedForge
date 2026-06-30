using System.Globalization;
using System.Text.Json;
using System.Xml;
using Microsoft.Extensions.Options;
using SeedForge.Domain;

namespace SeedForge.Services.YouTube
{
    /// <summary>
    /// Typed <see cref="HttpClient"/> for the YouTube Data API v3. Base address comes from <see cref="YouTubeOptions"/>
    /// (wired in <c>AddYouTube</c>); the API key is a query-string param appended to every request. Resolves a channel
    /// reference via <c>channels.list</c> (<c>id</c>/<c>forHandle</c>, with a <c>search.list</c> fallback for legacy
    /// custom references) and lists recent uploads via <c>playlistItems.list</c>. Throws <see cref="YouTubeException"/>
    /// on any non-2xx / unusable response. Discovery only — no transcript or AI work.
    /// </summary>
    public sealed class YouTubeDataClient(HttpClient http, IOptions<YouTubeOptions> options, ILogger<YouTubeDataClient> log)
        : IYouTubeDataClient
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        private readonly YouTubeOptions _opts = options.Value;

        public async Task<ResolvedChannel> ResolveChannelAsync(string input, CancellationToken ct = default)
        {
            var reference = ChannelRef.Parse(input);

            var query = reference.Kind switch
            {
                ChannelRefKind.Id => $"channels?part=snippet,contentDetails&id={Uri.EscapeDataString(reference.Value)}",
                ChannelRefKind.Handle => $"channels?part=snippet,contentDetails&forHandle={Uri.EscapeDataString(reference.Value)}",
                ChannelRefKind.Custom => await ResolveCustomToChannelQueryAsync(reference.Value, ct),
                _ => throw new YouTubeException($"Unsupported channel reference kind '{reference.Kind}'."),
            };

            using var doc = await GetAsync(query, ct);
            var items = RequireItems(doc, $"channel '{input}'");

            var channel = items[0];
            var channelId = channel.GetProperty("id").GetString()
                ?? throw new YouTubeException($"Channel '{input}' response had no id.");
            var title = channel.TryGetProperty("snippet", out var snippet) && snippet.TryGetProperty("title", out var t)
                ? t.GetString() ?? channelId
                : channelId;
            var uploads = channel.GetProperty("contentDetails")
                .GetProperty("relatedPlaylists")
                .GetProperty("uploads")
                .GetString()
                ?? throw new YouTubeException($"Channel '{input}' has no uploads playlist.");

            log.LogInformation("Resolved channel {Input} → {ChannelId} ({Title}), uploads {Uploads}",
                input, channelId, title, uploads);
            return new ResolvedChannel(channelId, title, uploads);
        }

        public async Task<IReadOnlyList<RecentUpload>> ListRecentUploadsAsync(string uploadsPlaylistId, CancellationToken ct = default)
        {
            // snippet carries the title; contentDetails carries the video id. Both parts cost the same single quota unit.
            var query = $"playlistItems?part=snippet,contentDetails&playlistId={Uri.EscapeDataString(uploadsPlaylistId)}&maxResults={_opts.MaxResults}";
            using var doc = await GetAsync(query, ct);

            var uploads = new List<RecentUpload>();
            if (doc.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    if (item.TryGetProperty("contentDetails", out var cd)
                        && cd.TryGetProperty("videoId", out var vid)
                        && vid.GetString() is { Length: > 0 } id)
                    {
                        var title = item.TryGetProperty("snippet", out var snippet) ? ReadString(snippet, "title") : null;
                        uploads.Add(new RecentUpload(id, title));
                    }
                }
            }

            log.LogInformation("Listed {Count} recent upload(s) for uploads playlist {Uploads}", uploads.Count, uploadsPlaylistId);
            return uploads;
        }

        private const int MaxIdsPerCall = 50; // videos.list accepts up to 50 ids in one (1-unit) call.

        public async Task<IReadOnlyDictionary<string, VideoMetadata>> GetVideoMetadataAsync(
            IEnumerable<string> videoIds, CancellationToken ct = default)
        {
            var ids = videoIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var result = new Dictionary<string, VideoMetadata>(StringComparer.Ordinal);
            if (ids.Count == 0) return result;

            // Batch into ≤50-id calls; each batch is a single quota unit.
            for (var offset = 0; offset < ids.Count; offset += MaxIdsPerCall)
            {
                var batch = ids.Skip(offset).Take(MaxIdsPerCall).ToList();
                var idParam = string.Join(",", batch.Select(Uri.EscapeDataString));
                var query = $"videos?part=snippet,contentDetails,statistics&id={idParam}&maxResults={MaxIdsPerCall}";

                using var doc = await GetAsync(query, ct);
                if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var item in items.EnumerateArray())
                {
                    if (item.TryGetProperty("id", out var idEl)
                        && idEl.ValueKind == JsonValueKind.String
                        && idEl.GetString() is { Length: > 0 } id)
                    {
                        result[id] = MapVideo(item);
                    }
                }
            }

            log.LogInformation("Fetched metadata for {Got}/{Asked} video id(s) via videos.list", result.Count, ids.Count);
            return result;
        }

        /// <summary>Maps one <c>videos.list</c> item to <see cref="VideoMetadata"/>, tolerating missing fields (null, never throw).</summary>
        private static VideoMetadata MapVideo(JsonElement item)
        {
            var snippet = item.TryGetProperty("snippet", out var s) && s.ValueKind == JsonValueKind.Object ? s : default;
            var stats = item.TryGetProperty("statistics", out var st) && st.ValueKind == JsonValueKind.Object ? st : default;
            var content = item.TryGetProperty("contentDetails", out var cd) && cd.ValueKind == JsonValueKind.Object ? cd : default;

            return new VideoMetadata(
                DurationSeconds: ReadIsoDuration(content),
                ViewCount: ReadLongString(stats, "viewCount"),
                LikeCount: ReadLongString(stats, "likeCount"),
                CommentCount: ReadLongString(stats, "commentCount"),
                PublishedAtUtc: ReadDate(snippet, "publishedAt"),
                Description: ReadString(snippet, "description"),
                ThumbnailUrl: ReadBestThumbnail(snippet),
                YouTubeChannelId: ReadString(snippet, "channelId"),
                Source: MetadataSource.YouTube);
        }

        /// <summary>contentDetails.duration is ISO-8601 ("PT15M33S"); convert to whole seconds via the in-box XmlConvert.</summary>
        private static int? ReadIsoDuration(JsonElement content)
        {
            if (content.ValueKind != JsonValueKind.Object
                || !content.TryGetProperty("duration", out var d)
                || d.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var iso = d.GetString();
            if (string.IsNullOrWhiteSpace(iso)) return null;
            try
            {
                return (int)XmlConvert.ToTimeSpan(iso).TotalSeconds;
            }
            catch (FormatException)
            {
                return null; // live streams report "P0D" / odd shapes — treat as unknown.
            }
        }

        /// <summary>YouTube statistics arrive as numeric strings; parse to long, null on absence (hidden likes stay null).</summary>
        private static long? ReadLongString(JsonElement obj, string key)
        {
            if (obj.ValueKind == JsonValueKind.Object
                && obj.TryGetProperty(key, out var el)
                && el.ValueKind == JsonValueKind.String
                && long.TryParse(el.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            {
                return n;
            }
            return null;
        }

        private static DateTime? ReadDate(JsonElement obj, string key)
        {
            if (obj.ValueKind == JsonValueKind.Object
                && obj.TryGetProperty(key, out var el)
                && el.ValueKind == JsonValueKind.String
                && DateTime.TryParse(el.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
            {
                return dt;
            }
            return null;
        }

        private static string? ReadString(JsonElement obj, string key)
        {
            if (obj.ValueKind == JsonValueKind.Object
                && obj.TryGetProperty(key, out var el)
                && el.ValueKind == JsonValueKind.String)
            {
                var v = el.GetString();
                return string.IsNullOrWhiteSpace(v) ? null : v;
            }
            return null;
        }

        /// <summary>snippet.thumbnails is an object keyed by size; pick the highest resolution available.</summary>
        private static string? ReadBestThumbnail(JsonElement snippet)
        {
            if (snippet.ValueKind != JsonValueKind.Object
                || !snippet.TryGetProperty("thumbnails", out var thumbs)
                || thumbs.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var size in new[] { "maxres", "standard", "high", "medium", "default" })
            {
                if (thumbs.TryGetProperty(size, out var t)
                    && t.ValueKind == JsonValueKind.Object
                    && t.TryGetProperty("url", out var u)
                    && u.ValueKind == JsonValueKind.String
                    && u.GetString() is { Length: > 0 } url)
                {
                    return url;
                }
            }
            return null;
        }

        /// <summary>Resolves a legacy custom reference to a channel id via <c>search.list</c> (the higher-quota fallback), then builds an id query.</summary>
        private async Task<string> ResolveCustomToChannelQueryAsync(string name, CancellationToken ct)
        {
            var search = $"search?part=snippet&type=channel&maxResults=1&q={Uri.EscapeDataString(name)}";
            using var doc = await GetAsync(search, ct);
            var items = RequireItems(doc, $"custom channel '{name}'");

            // search.list returns the channel id under id.channelId, not the top-level id.
            var channelId = items[0].GetProperty("id").GetProperty("channelId").GetString()
                ?? throw new YouTubeException($"Custom channel '{name}' search returned no channel id.");
            return $"channels?part=snippet,contentDetails&id={Uri.EscapeDataString(channelId)}";
        }

        /// <summary>GETs a relative API path (with the key appended) and returns the parsed document. Throws on non-2xx / non-JSON.</summary>
        private async Task<JsonDocument> GetAsync(string pathAndQuery, CancellationToken ct)
        {
            var url = $"{pathAndQuery}&key={Uri.EscapeDataString(_opts.ApiKey)}";

            HttpResponseMessage response;
            try
            {
                response = await http.GetAsync(url, ct);
            }
            catch (HttpRequestException ex)
            {
                throw new YouTubeException($"HTTP request to YouTube failed: {ex.Message}", inner: ex);
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new YouTubeException("YouTube request timed out.", inner: ex);
            }

            using (response)
            {
                var raw = await response.Content.ReadAsStringAsync(ct);
                if (!response.IsSuccessStatusCode)
                {
                    throw new YouTubeException(
                        $"YouTube returned {(int)response.StatusCode} {response.ReasonPhrase}.",
                        statusCode: (int)response.StatusCode,
                        responseBody: raw);
                }

                try
                {
                    return JsonDocument.Parse(raw);
                }
                catch (JsonException ex)
                {
                    throw new YouTubeException("YouTube response was not valid JSON.", responseBody: raw, inner: ex);
                }
            }
        }

        /// <summary>Returns the non-empty <c>items</c> array elements, or throws a clear <see cref="YouTubeException"/> when the reference resolved to nothing.</summary>
        private static List<JsonElement> RequireItems(JsonDocument doc, string what)
        {
            if (doc.RootElement.TryGetProperty("items", out var items)
                && items.ValueKind == JsonValueKind.Array
                && items.GetArrayLength() > 0)
            {
                return items.EnumerateArray().ToList();
            }
            throw new YouTubeException($"Could not resolve {what}: no matching channel.");
        }
    }
}
