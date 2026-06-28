namespace SeedForge.Services.Ai
{
    /// <summary>Raw result of an LLM call: the typed value plus the data the logger records (raw body + token usage).</summary>
    public sealed record LlmResult<T>(T Value, string RawResponse, Usage? Usage);
}
