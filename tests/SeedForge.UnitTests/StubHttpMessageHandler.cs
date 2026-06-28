using System.Net;
using System.Text;

namespace SeedForge.UnitTests
{
    /// <summary>Test handler that returns a canned response and captures the outgoing request body. Never touches the network.</summary>
    public sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _responseBody;
        private readonly Func<string, Task>? _onRequest;

        public string? LastRequestBody { get; private set; }
        public Uri? LastRequestUri { get; private set; }
        public int CallCount { get; private set; }

        public StubHttpMessageHandler(string responseBody, HttpStatusCode status = HttpStatusCode.OK, Func<string, Task>? onRequest = null)
        {
            _responseBody = responseBody;
            _status = status;
            _onRequest = onRequest;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequestUri = request.RequestUri;
            LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            if (_onRequest is not null && LastRequestBody is not null)
            {
                await _onRequest(LastRequestBody);
            }

            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_responseBody, Encoding.UTF8, "application/json"),
            };
        }

        /// <summary>A canned OpenAI-style response whose message content is the given (already-JSON or plain) string.</summary>
        public static string ChatResponse(string content, int promptTokens = 11, int completionTokens = 7)
        {
            var escaped = System.Text.Json.JsonSerializer.Serialize(content);
            return $$"""
            {
              "choices": [ { "message": { "role": "assistant", "content": {{escaped}} } } ],
              "usage": { "prompt_tokens": {{promptTokens}}, "completion_tokens": {{completionTokens}}, "total_tokens": {{promptTokens + completionTokens}} }
            }
            """;
        }
    }
}
