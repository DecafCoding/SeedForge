using System.Net;
using System.Text.Json.Nodes;
using SeedForge.Domain;
using SeedForge.Services.Ai;

namespace SeedForge.UnitTests
{
    /// <summary>Proves the client deserializes structured output, builds a strict body, caches the HttpClient, and surfaces failures.</summary>
    public class LlmClientTests
    {
        public sealed record Foo(string Title, int Count);

        private static readonly AiCallContext Ctx = new("corr-1", "Test", ModelSlot.Concept);

        private static LlmOptions LocalOptions(string baseUrl = "http://rig:8070") => new()
        {
            BaseUrl = baseUrl,
            ApiKey = "local",
            Model = "test-model",
        };

        private static IReadOnlyList<ChatMessage> Messages() =>
            new[] { new ChatMessage("system", "be terse"), new ChatMessage("user", "hi") };

        [Fact]
        public async Task CompleteStructuredAsync_returns_typed_object()
        {
            var handler = new StubHttpMessageHandler(StubHttpMessageHandler.ChatResponse("{\"title\":\"Widget\",\"count\":3}"));
            var client = new LlmClient(() => handler);

            var result = await client.CompleteStructuredAsync<Foo>(LocalOptions(), Messages(), Ctx);

            Assert.Equal("Widget", result.Title);
            Assert.Equal(3, result.Count);
        }

        [Fact]
        public async Task CompleteAsync_returns_trimmed_content()
        {
            var handler = new StubHttpMessageHandler(StubHttpMessageHandler.ChatResponse("  pong  "));
            var client = new LlmClient(() => handler);

            var result = await client.CompleteAsync(LocalOptions(), Messages(), Ctx);

            Assert.Equal("pong", result);
        }

        [Fact]
        public async Task Structured_body_is_strict_and_omits_null_temperature_in_JsonSchema_mode()
        {
            var handler = new StubHttpMessageHandler(StubHttpMessageHandler.ChatResponse("{\"title\":\"x\",\"count\":1}"));
            var client = new LlmClient(() => handler);
            var options = LocalOptions();
            options.StructuredOutput = StructuredOutputMode.JsonSchema;

            await client.CompleteStructuredAsync<Foo>(options, Messages(), Ctx);

            var body = JsonNode.Parse(handler.LastRequestBody!)!.AsObject();
            Assert.False(body.ContainsKey("temperature"));
            Assert.False(body.ContainsKey("reasoning_effort"));

            var schema = body["response_format"]!["json_schema"]!;
            Assert.True(schema["strict"]!.GetValue<bool>());
            Assert.False(schema["schema"]!["additionalProperties"]!.GetValue<bool>());
        }

        [Fact]
        public async Task Structured_default_mode_sends_no_response_format_and_describes_schema_in_prompt()
        {
            var handler = new StubHttpMessageHandler(StubHttpMessageHandler.ChatResponse("{\"title\":\"x\",\"count\":1}"));
            var client = new LlmClient(() => handler);

            // Default StructuredOutput is Prompt: tolerant of local servers that 500 on a json_schema response_format.
            await client.CompleteStructuredAsync<Foo>(LocalOptions(), Messages(), Ctx);

            var body = JsonNode.Parse(handler.LastRequestBody!)!.AsObject();
            Assert.False(body.ContainsKey("response_format"));

            var systemContent = body["messages"]!.AsArray()
                .First(m => m!["role"]!.GetValue<string>() == "system")!["content"]!.GetValue<string>();
            Assert.Contains("JSON Schema", systemContent);
            Assert.Contains("count", systemContent); // schema mentions the target type's properties
        }

        [Fact]
        public async Task Structured_default_mode_recovers_JSON_wrapped_in_fences_and_prose()
        {
            // A weak local model often emits the right JSON wrapped in a markdown fence with chatter around it.
            var reply = "Sure! Here is the result:\n```json\n{\"title\":\"Widget\",\"count\":3}\n```\nHope that helps.";
            var handler = new StubHttpMessageHandler(StubHttpMessageHandler.ChatResponse(reply));
            var client = new LlmClient(() => handler);

            var result = await client.CompleteStructuredAsync<Foo>(LocalOptions(), Messages(), Ctx);

            Assert.Equal("Widget", result.Title);
            Assert.Equal(3, result.Count);
        }

        [Fact]
        public async Task JsonObject_mode_sets_json_object_response_format()
        {
            var handler = new StubHttpMessageHandler(StubHttpMessageHandler.ChatResponse("{\"title\":\"x\",\"count\":1}"));
            var client = new LlmClient(() => handler);
            var options = LocalOptions();
            options.StructuredOutput = StructuredOutputMode.JsonObject;

            await client.CompleteStructuredAsync<Foo>(options, Messages(), Ctx);

            var body = JsonNode.Parse(handler.LastRequestBody!)!.AsObject();
            Assert.Equal("json_object", body["response_format"]!["type"]!.GetValue<string>());
        }

        [Fact]
        public async Task Body_includes_temperature_when_set()
        {
            var handler = new StubHttpMessageHandler(StubHttpMessageHandler.ChatResponse("ok"));
            var client = new LlmClient(() => handler);
            var options = LocalOptions();
            options.Temperature = 0.5;

            await client.CompleteAsync(options, Messages(), Ctx);

            var body = JsonNode.Parse(handler.LastRequestBody!)!.AsObject();
            Assert.Equal(0.5, body["temperature"]!.GetValue<double>());
        }

        [Fact]
        public async Task HttpClient_is_reused_for_identical_options_and_rebuilt_on_change()
        {
            var factoryCalls = 0;
            StubHttpMessageHandler MakeHandler()
            {
                factoryCalls++;
                return new StubHttpMessageHandler(StubHttpMessageHandler.ChatResponse("ok"));
            }

            var client = new LlmClient(MakeHandler);

            await client.CompleteAsync(LocalOptions(), Messages(), Ctx);
            await client.CompleteAsync(LocalOptions(), Messages(), Ctx);
            Assert.Equal(1, factoryCalls); // identical options ⇒ same cached HttpClient

            await client.CompleteAsync(LocalOptions("http://other:9090"), Messages(), Ctx);
            Assert.Equal(2, factoryCalls); // changed BaseUrl ⇒ rebuilt
        }

        [Fact]
        public async Task Non_2xx_throws_LlmException_with_status()
        {
            var handler = new StubHttpMessageHandler("{\"error\":\"boom\"}", HttpStatusCode.InternalServerError);
            var client = new LlmClient(() => handler);

            var ex = await Assert.ThrowsAsync<LlmException>(() => client.CompleteAsync(LocalOptions(), Messages(), Ctx));
            Assert.Equal(500, ex.StatusCode);
        }
    }
}
