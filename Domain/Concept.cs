namespace SeedForge.Domain
{
    /// <summary>Append-only derivative: a story concept built from an idea; many per idea, with one marked active.</summary>
    public class Concept
    {
        public int Id { get; set; }
        public int IdeaId { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Genre { get; set; } = string.Empty;
        public string Theme { get; set; } = string.Empty;
        public string Hook { get; set; } = string.Empty;
        public string Conflict { get; set; } = string.Empty;
        public string CentralStoryQuestion { get; set; } = string.Empty;
        public ModelSlot Slot { get; set; }
        public string Model { get; set; } = string.Empty;
        public string PromptVersion { get; set; } = string.Empty;
        public int? IdeaScoreId { get; set; }
        public bool IsActive { get; set; }
        public bool IsStale { get; set; }
        public DateTime CreatedAtUtc { get; set; }
    }
}
