namespace SeedForge.Services.Ai
{
    /// <summary>Per-call connection options for one model slot (bound from config, resolved fresh per call).</summary>
    public class LlmOptions
    {
        /// <summary>OpenAI-compatible base URL (e.g. http://host:port). Blank ⇒ hosted OpenAI.</summary>
        public string BaseUrl { get; set; } = string.Empty;

        /// <summary>Bearer API key. Local rigs accept any non-empty value; hosted keys come from user-secrets.</summary>
        public string ApiKey { get; set; } = string.Empty;

        /// <summary>Model identifier passed verbatim to the endpoint.</summary>
        public string Model { get; set; } = string.Empty;

        /// <summary>HTTP timeout in seconds (LLM calls can be slow; default 5 minutes).</summary>
        public int TimeoutSeconds { get; set; } = 300;

        /// <summary>Sampling temperature; omitted from the request when null (reasoning-locked models reject it).</summary>
        public double? Temperature { get; set; }

        /// <summary>Reasoning effort (e.g. "low"/"medium"/"high"); omitted from the request when null.</summary>
        public string? ReasoningEffort { get; set; }

        /// <summary>Returns a shallow copy so callers can mutate a per-call instance without leaking into shared config.</summary>
        public LlmOptions Copy() => new()
        {
            BaseUrl = BaseUrl,
            ApiKey = ApiKey,
            Model = Model,
            TimeoutSeconds = TimeoutSeconds,
            Temperature = Temperature,
            ReasoningEffort = ReasoningEffort,
        };
    }
}
