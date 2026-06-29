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

        // Queue mechanics (Phase 5): the ConceptJob row is the Concept-worker job.
        /// <summary>Number of failed build attempts; drives backoff and the terminal-Failed cap.</summary>
        public int AttemptCount { get; set; }
        /// <summary>Earliest UTC the job may be claimed again after a backoff; null ⇒ immediately eligible.</summary>
        public DateTime? NextAttemptUtc { get; set; }
        /// <summary>Higher claims first; "process now" raises this so the job jumps the line.</summary>
        public int Priority { get; set; }
    }
}
