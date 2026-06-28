namespace SeedForge.Domain
{
    /// <summary>Immutable source: a subject-bounded slice of a transcript with its character span.</summary>
    public class Segment
    {
        public int Id { get; set; }
        public int TranscriptId { get; set; }
        public int OrdinalIndex { get; set; }
        public int StartChar { get; set; }
        public int EndChar { get; set; }
        public string Text { get; set; } = string.Empty;
        public DateTime CreatedAtUtc { get; set; }
    }
}
