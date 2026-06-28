using SeedForge.Domain;

namespace SeedForge.Services.Ai
{
    /// <summary>Per-call metadata threaded through a run so the logger can correlate and attribute each LLM call.</summary>
    public sealed record AiCallContext(
        string CorrelationId,
        string Stage,
        ModelSlot Slot,
        string? RelatedEntityType = null,
        int? RelatedEntityId = null);
}
