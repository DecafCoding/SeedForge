using Microsoft.Extensions.Options;
using SeedForge.Domain;

namespace SeedForge.Services.Ai
{
    /// <summary>Resolves a fresh <see cref="LlmOptions"/> copy for a given <see cref="ModelSlot"/> from bound config.</summary>
    public sealed class LlmOptionsResolver(IOptionsMonitor<AiOptions> options)
    {
        private readonly IOptionsMonitor<AiOptions> _options = options;

        /// <summary>Returns a per-call copy of the slot's options so callers can mutate it without corrupting shared config.</summary>
        /// <exception cref="InvalidOperationException">Thrown when the slot has no configured options.</exception>
        public LlmOptions Resolve(ModelSlot slot)
        {
            var key = slot.ToString();
            if (!_options.CurrentValue.Slots.TryGetValue(key, out var configured))
            {
                throw new InvalidOperationException(
                    $"No LLM options configured for slot '{key}'. Add 'Ai:Slots:{key}' to configuration.");
            }

            // Return a copy, not the shared instance, so mutating Temperature/ReasoningEffort can't leak across slots.
            return new LlmOptions
            {
                BaseUrl = configured.BaseUrl,
                ApiKey = configured.ApiKey,
                Model = configured.Model,
                TimeoutSeconds = configured.TimeoutSeconds,
                Temperature = configured.Temperature,
                ReasoningEffort = configured.ReasoningEffort,
            };
        }
    }
}
