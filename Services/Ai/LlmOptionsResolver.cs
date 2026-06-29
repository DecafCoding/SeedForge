using Microsoft.Extensions.Options;
using SeedForge.Data;
using SeedForge.Domain;

namespace SeedForge.Services.Ai
{
    /// <summary>
    /// Resolves a fresh <see cref="LlmOptions"/> copy for a <see cref="ModelSlot"/> from the active (or a chosen)
    /// <see cref="ConfigProfile"/>, falling back to the bound <see cref="AiOptions"/> when no profile applies. A blank
    /// OpenAI slot key is filled from <c>Ai:OpenAiApiKey</c> (user-secrets) so secrets never live in the database.
    /// Scoped: it reads <see cref="ApplicationDbContext"/> per call.
    /// </summary>
    public sealed class LlmOptionsResolver(
        ApplicationDbContext db, IOptionsMonitor<AiOptions> options, IConfiguration config)
    {
        private readonly ApplicationDbContext _db = db;
        private readonly IOptionsMonitor<AiOptions> _options = options;
        private readonly IConfiguration _config = config;

        /// <summary>
        /// Returns a per-call copy of the slot's options. Reads the active profile (or <paramref name="profileId"/>
        /// when given); falls back to appsettings when no profile applies. Fills a blank OpenAI key from user-secrets.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown when the slot has no configured options anywhere.</exception>
        public LlmOptions Resolve(ModelSlot slot, int? profileId = null)
        {
            var key = slot.ToString();

            var profile = profileId is int id
                ? _db.ConfigProfiles.Find(id)
                : _db.ConfigProfiles.FirstOrDefault(p => p.IsActive);

            LlmOptions? configured = null;
            if (profile is not null)
            {
                ProfileSlots.Deserialize(profile.SlotsJson).TryGetValue(key, out configured);
            }

            // Fall back to appsettings when there is no profile, or the profile omits this slot.
            if (configured is null && !_options.CurrentValue.Slots.TryGetValue(key, out configured))
            {
                throw new InvalidOperationException(
                    $"No LLM options configured for slot '{key}'. Add a profile slot or 'Ai:Slots:{key}' to configuration.");
            }

            // Copy so mutating Temperature/ReasoningEffort/ApiKey can't leak across slots or into shared config.
            var resolved = configured.Copy();

            // Secrets stay out of the DB: fill a blank OpenAI key from user-secrets at resolve time.
            if (string.IsNullOrEmpty(resolved.ApiKey) && IsOpenAi(resolved.BaseUrl))
            {
                resolved.ApiKey = _config["Ai:OpenAiApiKey"] ?? string.Empty;
            }

            return resolved;
        }

        /// <summary>A blank base URL means hosted OpenAI; an explicit openai.com URL is also OpenAI.</summary>
        private static bool IsOpenAi(string baseUrl) =>
            string.IsNullOrWhiteSpace(baseUrl) || baseUrl.Contains("openai.com", StringComparison.OrdinalIgnoreCase);
    }
}
