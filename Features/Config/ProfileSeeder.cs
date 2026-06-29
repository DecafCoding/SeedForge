using Microsoft.EntityFrameworkCore;
using SeedForge.Data;
using SeedForge.Domain;
using SeedForge.Services.Ai;

namespace SeedForge.Features.Config
{
    /// <summary>
    /// First-run seeder for the three canonical <see cref="ConfigProfile"/>s. Idempotent: seeds only when no
    /// profile exists. "All Local" mirrors the bound <see cref="AiOptions"/> reference rig so switching to it
    /// reproduces pre-profile behavior exactly. OpenAI slots ship with a blank key (the resolver fills it from
    /// user-secrets) — no real secret is ever stored in a profile row.
    /// </summary>
    public static class ProfileSeeder
    {
        public const string AllLocal = "All Local";
        public const string LocalPlusOpenAiConcept = "Local + OpenAI Concept";
        public const string AllOpenAi = "All OpenAI";

        /// <summary>Placeholder OpenAI model name in the seeded profiles; the user edits these on /config.</summary>
        public const string DefaultOpenAiModel = "gpt-4o";

        public static async Task SeedAsync(ApplicationDbContext db, AiOptions appsettings, CancellationToken ct = default)
        {
            if (await db.ConfigProfiles.AnyAsync(ct))
            {
                return; // idempotent: profiles already seeded
            }

            // "All Local" — mirror the appsettings reference rig exactly so it reproduces pre-profile behavior.
            var allLocal = appsettings.Slots.ToDictionary(kv => kv.Key, kv => kv.Value.Copy());

            // "Local + OpenAI Concept" — Concept slot targets OpenAI (blank base ⇒ hosted), others stay local.
            var localPlusConcept = appsettings.Slots.ToDictionary(kv => kv.Key, kv => kv.Value.Copy());
            localPlusConcept[ModelSlot.Concept.ToString()] = OpenAiSlot();

            // "All OpenAI" — every configured slot targets OpenAI with a blank key.
            var allOpenAi = appsettings.Slots.Keys.ToDictionary(k => k, _ => OpenAiSlot());

            var now = DateTime.UtcNow;
            db.ConfigProfiles.AddRange(
                new ConfigProfile { Name = AllLocal, IsActive = true, SlotsJson = ProfileSlots.Serialize(allLocal), CreatedAtUtc = now },
                new ConfigProfile { Name = LocalPlusOpenAiConcept, IsActive = false, SlotsJson = ProfileSlots.Serialize(localPlusConcept), CreatedAtUtc = now },
                new ConfigProfile { Name = AllOpenAi, IsActive = false, SlotsJson = ProfileSlots.Serialize(allOpenAi), CreatedAtUtc = now });

            await db.SaveChangesAsync(ct);
        }

        /// <summary>An OpenAI-targeting slot: blank BaseUrl (⇒ hosted OpenAI) and blank key (filled from user-secrets).</summary>
        private static LlmOptions OpenAiSlot() => new()
        {
            BaseUrl = string.Empty,
            ApiKey = string.Empty,
            Model = DefaultOpenAiModel,
            TimeoutSeconds = 300,
        };
    }
}
