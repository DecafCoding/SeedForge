using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace SeedForge.Services.Apify
{
    /// <summary>The dataset items returned by a synchronous actor run. Cost is not carried here — the
    /// <c>run-sync-get-dataset-items</c> endpoint returns items, not usage, so cost is derived deterministically
    /// from the actor's pay-per-result price (see <see cref="ApifyOptions.UsdPerThousandVideos"/>).</summary>
    public sealed record ApifyDatasetResult(JsonElement Items);

    /// <summary>
    /// Typed <see cref="HttpClient"/> for the Apify <c>run-sync-get-dataset-items</c> endpoint of one actor.
    /// Base address + timeout come from <see cref="ApifyOptions"/> (wired in <c>AddApifyServices</c>); the token is a
    /// query-string param, not a header. Throws <see cref="ApifyException"/> on any non-2xx / unusable response.
    /// </summary>
    public sealed class ApifyClient(HttpClient http, IOptions<ApifyOptions> options, ILogger<ApifyClient> log)
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        /// <summary>POSTs the actor input to <c>v2/acts/{ActorId}/run-sync-get-dataset-items?token=…</c> and returns the parsed JSON array root.</summary>
        public async Task<ApifyDatasetResult> RunSyncGetDatasetItemsAsync(object input, CancellationToken ct = default)
        {
            var o = options.Value;

            // The token is a query-string param; ActorId already carries the literal '~' and must NOT be re-encoded.
            var path = $"v2/acts/{o.ActorId}/run-sync-get-dataset-items?token={Uri.EscapeDataString(o.Token)}";
            using var content = new StringContent(JsonSerializer.Serialize(input, Json), Encoding.UTF8, "application/json");

            HttpResponseMessage response;
            try
            {
                response = await http.PostAsync(path, content, ct);
            }
            catch (HttpRequestException ex)
            {
                throw new ApifyException($"HTTP request to Apify failed: {ex.Message}", inner: ex);
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new ApifyException("Apify request timed out.", inner: ex);
            }

            using (response)
            {
                var raw = await response.Content.ReadAsStringAsync(ct);
                if (!response.IsSuccessStatusCode)
                {
                    throw new ApifyException(
                        $"Apify returned {(int)response.StatusCode} {response.ReasonPhrase}.",
                        statusCode: (int)response.StatusCode,
                        responseBody: raw);
                }

                JsonElement root;
                try
                {
                    // Clone so the JsonElement survives the JsonDocument being disposed.
                    using var doc = JsonDocument.Parse(raw);
                    root = doc.RootElement.Clone();
                }
                catch (JsonException ex)
                {
                    throw new ApifyException("Apify response was not valid JSON.", responseBody: raw, inner: ex);
                }

                log.LogInformation("Apify run-sync returned {Count} dataset item(s)",
                    root.ValueKind == JsonValueKind.Array ? root.GetArrayLength() : 0);

                return new ApifyDatasetResult(root);
            }
        }
    }
}
