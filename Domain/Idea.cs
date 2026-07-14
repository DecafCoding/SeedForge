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

        /// <summary>Manual Keep/Skip verdict; the sole gate to concept creation (see <see cref="IdeaDisposition"/>).</summary>
        public IdeaDisposition Disposition { get; set; } = IdeaDisposition.Undecided;

        /// <summary>When the user last set <see cref="Disposition"/>; null while Undecided.</summary>
        public DateTime? DispositionAtUtc { get; set; }
    }
}
