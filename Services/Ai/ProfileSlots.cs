using System.Text.Json;

namespace SeedForge.Services.Ai
{
    /// <summary>
    /// (De)serialization for the per-slot bundle stored in <c>ConfigProfile.SlotsJson</c> — a
    /// <c>Dictionary&lt;string, LlmOptions&gt;</c> keyed by <see cref="SeedForge.Domain.ModelSlot"/> name. Lives in
    /// Services (with <see cref="LlmOptions"/>) so the resolver can read it without depending on Features.
    /// </summary>
    public static class ProfileSlots
    {
        /// <summary>Serializes a per-slot bundle for storage in a profile row.</summary>
        public static string Serialize(IReadOnlyDictionary<string, LlmOptions> slots) =>
            JsonSerializer.Serialize(slots);

        /// <summary>Deserializes a profile's slot bundle; never returns null.</summary>
        public static Dictionary<string, LlmOptions> Deserialize(string slotsJson) =>
            string.IsNullOrWhiteSpace(slotsJson)
                ? new()
                : JsonSerializer.Deserialize<Dictionary<string, LlmOptions>>(slotsJson) ?? new();
    }
}
