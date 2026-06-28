namespace SeedForge.Domain
{
    /// <summary>Processing-worker state for a discovered <see cref="Video"/>.</summary>
    public enum VideoJobStatus
    {
        Pending,
        InProgress,
        Done,
        Failed,
        NoTranscript
    }

    /// <summary>State of a concept-building <see cref="ConceptJob"/>.</summary>
    public enum ConceptJobStatus
    {
        Pending,
        InProgress,
        Done,
        Failed
    }

    /// <summary>What caused a <see cref="ConceptJob"/> to be enqueued.</summary>
    public enum ConceptTrigger
    {
        Auto,
        Regen
    }

    /// <summary>A pipeline stage's model slot, used to resolve LLM options per call.</summary>
    public enum ModelSlot
    {
        Seed,
        Extraction,
        Scoring,
        Concept,
        Conversation
    }
}
