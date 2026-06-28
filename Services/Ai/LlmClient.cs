using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SeedForge.Services.Ai
{
    /// <summary>
    /// Thread-safe singleton client for any OpenAI-compatible /chat/completions endpoint.
    /// Caches one <see cref="HttpClient"/> keyed on (BaseUrl, ApiKey, TimeoutSeconds) and rebuilds it
    /// on change by dropping — not disposing — the old one so in-flight calls survive.
    /// </summary>
    public sealed class LlmClient : ILlmClient
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        private readonly Func<HttpMessageHandler> _handlerFactory;
        private readonly object _gate = new();
        private HttpClient? _client;
        private string? _cacheKey;

        public LlmClient(Func<HttpMessageHandler>? handlerFactory = null)
        {
            _handlerFactory = handlerFactory ?? (() => new SocketsHttpHandler());
        }

        public async Task<string> CompleteAsync(
            LlmOptions options, IReadOnlyList<ChatMessage> messages, AiCallContext context, CancellationToken ct = default)
            => (await CompleteRawAsync(options, messages, context, ct)).Value;

        public async Task<T> CompleteStructuredAsync<T>(
            LlmOptions options, IReadOnlyList<ChatMessage> messages, AiCallContext context, CancellationToken ct = default)
            => (await CompleteStructuredRawAsync<T>(options, messages, context, ct)).Value;

        /// <summary>Free-text completion exposing raw body + usage for the logging decorator.</summary>
        internal async Task<LlmResult<string>> CompleteRawAsync(
            LlmOptions options, IReadOnlyList<ChatMessage> messages, AiCallContext context, CancellationToken ct = default)
        {
            var (parsed, raw) = await PostAsync(options, BuildBody(options, messages, responseFormat: null), ct);
            var content = ExtractContent(parsed, raw);
            return new LlmResult<string>(content.Trim(), raw, parsed.Usage);
        }

        /// <summary>Strict-structured completion: deserializes the response content into <typeparamref name="T"/>.</summary>
        internal async Task<LlmResult<T>> CompleteStructuredRawAsync<T>(
            LlmOptions options, IReadOnlyList<ChatMessage> messages, AiCallContext context, CancellationToken ct = default)
        {
            var responseFormat = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject
                {
                    ["name"] = typeof(T).Name,
                    ["strict"] = true,
                    ["schema"] = JsonSchemaGenerator.ForType<T>(),
                },
            };

            var (parsed, raw) = await PostAsync(options, BuildBody(options, messages, responseFormat), ct);
            var content = ExtractContent(parsed, raw);

            T? value;
            try
            {
                value = JsonSerializer.Deserialize<T>(content, Json);
            }
            catch (JsonException ex)
            {
                throw new LlmException("Structured response content was not valid JSON for the target type.", responseBody: content, inner: ex);
            }

            if (value is null)
            {
                throw new LlmException("Structured response deserialized to null.", responseBody: content);
            }

            return new LlmResult<T>(value, raw, parsed.Usage);
        }

        /// <summary>Builds the request body, omitting temperature/reasoning_effort when null (reasoning-locked models reject them).</summary>
        private static JsonObject BuildBody(LlmOptions options, IReadOnlyList<ChatMessage> messages, JsonObject? responseFormat)
        {
            var messageArray = new JsonArray();
            foreach (var message in messages)
            {
                messageArray.Add(new JsonObject { ["role"] = message.Role, ["content"] = message.Content });
            }

            var body = new JsonObject
            {
                ["model"] = options.Model,
                ["messages"] = messageArray,
            };

            if (options.Temperature is double temperature) body["temperature"] = temperature;
            if (options.ReasoningEffort is string effort) body["reasoning_effort"] = effort;
            if (responseFormat is not null) body["response_format"] = responseFormat;

            return body;
        }

        private async Task<(ChatCompletionResponse Parsed, string Raw)> PostAsync(LlmOptions options, JsonObject body, CancellationToken ct)
        {
            var client = GetClient(options);
            using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

            HttpResponseMessage response;
            try
            {
                response = await client.PostAsync("chat/completions", content, ct);
            }
            catch (HttpRequestException ex)
            {
                throw new LlmException($"HTTP request to the model endpoint failed: {ex.Message}", inner: ex);
            }

            using (response)
            {
                var raw = await response.Content.ReadAsStringAsync(ct);
                if (!response.IsSuccessStatusCode)
                {
                    throw new LlmException(
                        $"Model endpoint returned {(int)response.StatusCode} {response.ReasonPhrase}.",
                        statusCode: (int)response.StatusCode,
                        responseBody: raw);
                }

                ChatCompletionResponse? parsed;
                try
                {
                    parsed = JsonSerializer.Deserialize<ChatCompletionResponse>(raw, Json);
                }
                catch (JsonException ex)
                {
                    throw new LlmException("Model response was not valid JSON.", responseBody: raw, inner: ex);
                }

                if (parsed is null)
                {
                    throw new LlmException("Model response deserialized to null.", responseBody: raw);
                }

                return (parsed, raw);
            }
        }

        private static string ExtractContent(ChatCompletionResponse parsed, string raw)
        {
            var content = parsed.Choices.Count > 0 ? parsed.Choices[0].Message?.Content : null;
            if (content is null)
            {
                throw new LlmException("Model response contained no message content.", responseBody: raw);
            }
            return content;
        }

        /// <summary>Returns the cached <see cref="HttpClient"/>, rebuilding it only when the connection key changes.</summary>
        private HttpClient GetClient(LlmOptions options)
        {
            var key = $"{options.BaseUrl}|{options.ApiKey}|{options.TimeoutSeconds}";
            lock (_gate)
            {
                if (_client is null || _cacheKey != key)
                {
                    var client = new HttpClient(_handlerFactory())
                    {
                        BaseAddress = new Uri(ResolveBaseAddress(options.BaseUrl)),
                        Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds),
                    };
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);

                    // Drop (do not dispose) the previous client so any in-flight requests on it complete.
                    _client = client;
                    _cacheKey = key;
                }

                return _client;
            }
        }

        /// <summary>Blank BaseUrl ⇒ hosted OpenAI; otherwise the configured URL used verbatim (trailing slash ensured for relative POSTs).</summary>
        private static string ResolveBaseAddress(string baseUrl)
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                return "https://api.openai.com/v1/";
            }
            return baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/";
        }
    }
}
