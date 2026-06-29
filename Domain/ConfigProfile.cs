namespace SeedForge.Domain
{
    /// <summary>
    /// A named, switchable bundle of per-slot LLM settings. <see cref="SlotsJson"/> holds a serialized
    /// <c>Dictionary&lt;string, LlmOptions&gt;</c> keyed by <see cref="ModelSlot"/> name (kept as a string so the
    /// Domain stays free of a Services dependency). At most one profile is active at a time.
    /// </summary>
    public class ConfigProfile
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public bool IsActive { get; set; }
        public string SlotsJson { get; set; } = string.Empty;
        public DateTime CreatedAtUtc { get; set; }
    }
}
