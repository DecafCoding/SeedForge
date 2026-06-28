namespace SeedForge.Services.Ai
{
    /// <summary>Root AI options bound from the "Ai" config section: one <see cref="LlmOptions"/> per slot.</summary>
    public class AiOptions
    {
        /// <summary>Slot name (matching <see cref="SeedForge.Domain.ModelSlot"/>) → connection options.</summary>
        public Dictionary<string, LlmOptions> Slots { get; set; } = new();
    }
}
