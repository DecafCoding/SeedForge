using System.Text.Json.Serialization;

namespace SeedForge.Services.Ai
{
    /// <summary>Deserialized shape of an OpenAI-compatible /chat/completions response.</summary>
    public sealed class ChatCompletionResponse
    {
        [JsonPropertyName("choices")]
        public List<Choice> Choices { get; set; } = new();

        [JsonPropertyName("usage")]
        public Usage? Usage { get; set; }
    }

    /// <summary>One completion choice.</summary>
    public sealed class Choice
    {
        [JsonPropertyName("message")]
        public ResponseMessage? Message { get; set; }
    }

    /// <summary>The assistant message content within a choice.</summary>
    public sealed class ResponseMessage
    {
        [JsonPropertyName("content")]
        public string? Content { get; set; }
    }

    /// <summary>Token accounting reported by the endpoint.</summary>
    public sealed class Usage
    {
        [JsonPropertyName("prompt_tokens")]
        public int PromptTokens { get; set; }

        [JsonPropertyName("completion_tokens")]
        public int CompletionTokens { get; set; }

        [JsonPropertyName("total_tokens")]
        public int TotalTokens { get; set; }
    }
}
