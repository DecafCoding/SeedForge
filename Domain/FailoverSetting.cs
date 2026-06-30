namespace SeedForge.Domain
{
    /// <summary>
    /// Single-row global setting (<see cref="Id"/> is always 1): when a model endpoint is unreachable, retry the failed
    /// call once against <see cref="FallbackProfileId"/>'s matching slot. Disabled by default so a hosted provider is
    /// never used without an explicit opt-in. <see cref="FallbackProfileId"/> points at a <see cref="ConfigProfile"/>
    /// (e.g. "All OpenAI"); a null or dangling id means "no failover".
    /// </summary>
    public class FailoverSetting
    {
        public int Id { get; set; }
        public bool Enabled { get; set; }
        public int? FallbackProfileId { get; set; }
    }
}
