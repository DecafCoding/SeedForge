using System.Text.Json;
using Microsoft.Extensions.Options;

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

        public async Task<IReadOnlyList<string>> ListRecentVideoIdsAsync(string uploadsPlaylistId, CancellationToken ct = default)
        {
            var query = $"playlistItems?part=contentDetails&playlistId={Uri.EscapeDataString(uploadsPlaylistId)}&maxResults={_opts.MaxResults}";
            using var doc = await GetAsync(query, ct);

            var ids = new List<string>();
            if (doc.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    if (item.TryGetProperty("contentDetails", out var cd)
                        && cd.TryGetProperty("videoId", out var vid)
                        && vid.GetString() is { Length: > 0 } id)
                    {
                        ids.Add(id);
                    }
                }
            }

            log.LogInformation("Listed {Count} recent video id(s) for uploads playlist {Uploads}", ids.Count, uploadsPlaylistId);
            return ids;
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
