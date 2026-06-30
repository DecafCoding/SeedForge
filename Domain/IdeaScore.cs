namespace SeedForge.Domain
{
    /// <summary>Append-only derivative: axis scores and threshold result for an idea, tagged with the slot/model that produced it.</summary>
    public class IdeaScore
    {
        public int Id { get; set; }
        public int IdeaId { get; set; }
        public double Novelty { get; set; }
        public double Coherence { get; set; }
        public double Potential { get; set; }
        public double Suitability { get; set; }
        public bool PassedThreshold { get; set; }
        public ModelSlot Slot { get; set; }
        public string Model { get; set; } = string.Empty;
        public string PromptVersion { get; set; } = string.Empty;
        public DateTime CreatedAtUtc { get; set; }
    }
}
