namespace SeedForge.Domain
{
    /// <summary>Append-only derivative: per-call observability for one LLM call, joinable to a run via correlation id.</summary>
    public class AiCallLog
    {
        public int Id { get; set; }
        public string CorrelationId { get; set; } = string.Empty;
        public string Stage { get; set; } = string.Empty;
        public ModelSlot Slot { get; set; }
        public bool IsLocal { get; set; }
        public string BaseUrl { get; set; } = string.Empty;
        public string Model { get; set; } = string.Empty;
        public string SystemMessage { get; set; } = string.Empty;
        public string UserMessage { get; set; } = string.Empty;
        public string RawResponse { get; set; } = string.Empty;
        public int PromptTokens { get; set; }
        public int CompletionTokens { get; set; }
        public int TotalTokens { get; set; }
        public long LatencyMs { get; set; }
        public double EstimatedCost { get; set; }
        public double? Temperature { get; set; }
        public string? ReasoningEffort { get; set; }
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public string? RelatedEntityType { get; set; }
        public int? RelatedEntityId { get; set; }
        public DateTime CreatedAtUtc { get; set; }
    }
}
