namespace SeedForge.Domain
{
    /// <summary>Append-only derivative: a unit of concept-building work referencing one idea.</summary>
    public class ConceptJob
    {
        public int Id { get; set; }
        public int IdeaId { get; set; }
        public ConceptJobStatus Status { get; set; }
        public ConceptTrigger Trigger { get; set; }
        public ModelSlot? SlotOverride { get; set; }
        public string? ProfileOverride { get; set; }
        public DateTime CreatedAtUtc { get; set; }
    }
}
