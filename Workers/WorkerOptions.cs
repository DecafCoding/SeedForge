namespace SeedForge.Workers
{
    /// <summary>
    /// Strongly-typed config for the background workers and queue backoff (bound from the "Workers" section).
    /// Defaults are conservative for a single local server; tune in appsettings without code changes.
    /// </summary>
    public class WorkerOptions
    {
        /// <summary>How often the Processing worker wakes to drain the video queue (default 30 min).</summary>
        public int ProcessingIntervalSeconds { get; set; } = 1800;

        /// <summary>How often the Concept worker wakes to drain the concept queue (default 1 min).</summary>
        public int ConceptIntervalSeconds { get; set; } = 60;

        /// <summary>How often the Discovery worker wakes to poll the channel library for new uploads (default 1 day).</summary>
        public int DiscoveryIntervalSeconds { get; set; } = 86400;

        /// <summary>Failed attempts before a job becomes terminal <c>Failed</c>.</summary>
        public int MaxAttempts { get; set; } = 5;

        /// <summary>Base seconds for the exponential backoff (<c>BackoffBaseSeconds × 2^attempt</c>).</summary>
        public int BackoffBaseSeconds { get; set; } = 30;
    }
}
