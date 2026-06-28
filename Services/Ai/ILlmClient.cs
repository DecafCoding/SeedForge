namespace SeedForge.Services.Ai
{
    /// <summary>The network boundary for LLM calls: free-text and strict-structured completions over any OpenAI-compatible endpoint.</summary>
    public interface ILlmClient
    {
        /// <summary>Returns the trimmed free-text completion for the given messages.</summary>
        Task<string> CompleteAsync(
            LlmOptions options,
            IReadOnlyList<ChatMessage> messages,
            AiCallContext context,
            CancellationToken ct = default);

        /// <summary>Returns a typed object deserialized from a strict <c>json_schema</c> structured completion.</summary>
        Task<T> CompleteStructuredAsync<T>(
            LlmOptions options,
            IReadOnlyList<ChatMessage> messages,
            AiCallContext context,
            CancellationToken ct = default);
    }
}
