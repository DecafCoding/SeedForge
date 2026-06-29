namespace SeedForge.Domain
{
    /// <summary>Processing-worker state for a discovered <see cref="Video"/>.</summary>
    public enum VideoJobStatus
    {
        Pending,
        InProgress,
        Done,
        Failed,
        NoTranscript,
        ProcessedNoIdeas
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

    /// <summary>Where a <see cref="Video"/>'s captured metadata came from (provenance for freshness/precedence).</summary>
    public enum MetadataSource
    {
        /// <summary>No metadata captured yet.</summary>
        None,
        /// <summary>Parsed from the stored Apify dataset item (free; the default at ingest).</summary>
        Apify,
        /// <summary>Fetched from the YouTube Data API <c>videos.list</c> call (fresher; optional).</summary>
        YouTube,
        /// <summary>Both sources contributed values, resolved by the merge rule.</summary>
        Merged
    }
}
