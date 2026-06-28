namespace SeedForge.Domain
{
    /// <summary>Immutable source: the thin premise extracted from a segment — the durable lineage anchor.</summary>
    public class Idea
    {
        public int Id { get; set; }
        public int SegmentId { get; set; }
        public string Premise { get; set; } = string.Empty;
        public string CorrelationId { get; set; } = string.Empty;
        public string? ExtractionPromptVersion { get; set; }
        public DateTime CreatedAtUtc { get; set; }
    }
}
