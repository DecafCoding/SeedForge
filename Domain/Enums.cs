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
        ProcessedNoIdeas,

        /// <summary>
        /// Discovered but under the minimum duration (<see cref="Services.YouTube.YouTubeOptions.MinVideoDurationSeconds"/>).
        /// Kept only as a dedup marker so it is never re-discovered; never queued, never transcribed, hidden from the library.
        /// </summary>
        SkippedShort
    }

    /// <summary>State of a concept-building <see cref="ConceptJob"/>.</summary>
    public enum ConceptJobStatus
    {
        Pending,
        InProgress,
        Done,
        Failed
    }

    /// <summary>
    /// Manual user verdict on an <see cref="Idea"/> — distinct from the automated score's pass/fail.
    /// Keep is the sole gate to concept creation (it enqueues a <see cref="ConceptJob"/>), and can
    /// rescue an idea whose latest score failed; Skip never builds.
    /// </summary>
    public enum IdeaDisposition
    {
        /// <summary>Default — survivors wait here until the user decides.</summary>
        Undecided,
        /// <summary>Build a concept (works even when the latest score failed the threshold).</summary>
        Keep,
        /// <summary>Never build a concept for this idea.</summary>
        Skip
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
