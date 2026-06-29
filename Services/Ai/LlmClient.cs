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

        /// <summary>
        /// Structured completion: deserializes the response content into <typeparamref name="T"/>. The request strategy
        /// follows <see cref="LlmOptions.StructuredOutput"/> — strict <c>json_schema</c> only when explicitly chosen;
        /// otherwise the schema is described in the prompt and the reply is parsed defensively (tolerant of models that
        /// wrap JSON in markdown fences or surrounding prose).
        /// </summary>
        internal async Task<LlmResult<T>> CompleteStructuredRawAsync<T>(
            LlmOptions options, IReadOnlyList<ChatMessage> messages, AiCallContext context, CancellationToken ct = default)
        {
            var schema = JsonSchemaGenerator.ForType<T>();

            JsonObject? responseFormat = options.StructuredOutput switch
            {
                StructuredOutputMode.JsonSchema => new JsonObject
                {
                    ["type"] = "json_schema",
                    ["json_schema"] = new JsonObject
                    {
                        ["name"] = typeof(T).Name,
                        ["strict"] = true,
                        ["schema"] = schema,
                    },
                },
                StructuredOutputMode.JsonObject => new JsonObject { ["type"] = "json_object" },
                _ => null,
            };

            // Strict json_schema servers constrain generation themselves; every other mode must ask in the prompt.
            var effectiveMessages = options.StructuredOutput == StructuredOutputMode.JsonSchema
                ? messages
                : WithSchemaInstruction(messages, typeof(T).Name, schema);

            var (parsed, raw) = await PostAsync(options, BuildBody(options, effectiveMessages, responseFormat), ct);
            var content = ExtractContent(parsed, raw);
            var json = ExtractJson(content);

            T? value;
            try
            {
                value = JsonSerializer.Deserialize<T>(json, Json);
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

        /// <summary>
        /// Folds a "respond with only JSON matching this schema" instruction into the system message (or prepends one),
        /// so prompt/json_object modes still steer the model toward a parseable, schema-shaped reply.
        /// </summary>
        private static IReadOnlyList<ChatMessage> WithSchemaInstruction(IReadOnlyList<ChatMessage> messages, string name, JsonNode schema)
        {
            var instruction =
                $"You must respond with a single JSON value that conforms to this JSON Schema for \"{name}\". " +
                "Output only the raw JSON — no markdown code fences, no commentary, and no text before or after it.\n\n" +
                "JSON Schema:\n" + schema.ToJsonString();

            var list = new List<ChatMessage>(messages);
            var systemIndex = list.FindIndex(m => m.Role == "system");
            if (systemIndex >= 0)
            {
                list[systemIndex] = list[systemIndex] with { Content = list[systemIndex].Content + "\n\n" + instruction };
            }
            else
            {
                list.Insert(0, new ChatMessage("system", instruction));
            }

            return list;
        }

        /// <summary>
        /// Recovers the JSON payload from a model reply that may be fenced (```json … ```) or surrounded by prose:
        /// strips a leading/trailing code fence, then narrows to the outermost object/array. Returns the content
        /// unchanged when no JSON delimiters are present (so deserialization surfaces a clear error).
        /// </summary>
        private static string ExtractJson(string content)
        {
            var text = content.Trim();

            if (text.StartsWith("```", StringComparison.Ordinal))
            {
                var firstNewline = text.IndexOf('\n');
                if (firstNewline >= 0)
                {
                    text = text[(firstNewline + 1)..];
                }
                var closingFence = text.LastIndexOf("```", StringComparison.Ordinal);
                if (closingFence >= 0)
                {
                    text = text[..closingFence];
                }
                text = text.Trim();
            }

            var brace = text.IndexOf('{');
            var bracket = text.IndexOf('[');
            int start;
            char close;
            if (brace >= 0 && (bracket < 0 || brace < bracket))
            {
                start = brace;
                close = '}';
            }
            else if (bracket >= 0)
            {
                start = bracket;
                close = ']';
            }
            else
            {
                return text;
            }

            var end = text.LastIndexOf(close);
            return end > start ? text[start..(end + 1)] : text;
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
