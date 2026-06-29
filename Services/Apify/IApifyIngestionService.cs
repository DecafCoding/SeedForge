namespace SeedForge.Services.Apify
{
    /// <summary>
    /// The mockable Apify network boundary (Claude.md — interfaces only at the network boundary). Slices depend on this;
    /// the concrete <see cref="ApifyIngestionService"/> is tested against a stubbed <c>HttpMessageHandler</c>.
    /// </summary>
    public interface IApifyIngestionService
    {
        /// <summary>Resolves a YouTube URL/id, runs the actor, and returns the parsed transcript + metadata. Throws <see cref="ApifyException"/> on a fetch error.</summary>
        Task<IngestedVideo> FetchAsync(string urlOrId, CancellationToken ct = default);
    }
}
