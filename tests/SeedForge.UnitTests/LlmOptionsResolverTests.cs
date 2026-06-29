using SeedForge.Domain;
using SeedForge.Features.Config;
using SeedForge.Services.Ai;
using SeedForge.UnitTests.Fakes;

namespace SeedForge.UnitTests
{
    /// <summary>Proves the resolver reads the active/chosen profile, falls back to appsettings, and fills the OpenAI key.</summary>
    public class LlmOptionsResolverTests
    {
        [Fact]
        public void With_no_profiles_resolves_from_appsettings()
        {
            using var h = new ResolverHarness(ResolverHarness.RigOptions());
            Assert.Equal("Extraction-model", h.Resolver.Resolve(ModelSlot.Extraction).Model);
        }

        [Fact]
        public void Resolve_returns_a_copy_not_the_shared_instance()
        {
            using var h = new ResolverHarness(ResolverHarness.RigOptions());
            var first = h.Resolver.Resolve(ModelSlot.Concept);
            first.Temperature = 0.9;
            Assert.Null(h.Resolver.Resolve(ModelSlot.Concept).Temperature);
        }

        [Fact]
        public void Resolve_throws_for_unconfigured_slot_with_no_profile()
        {
            var options = new AiOptions
            {
                Slots = { ["Extraction"] = new LlmOptions { BaseUrl = "http://rig", Model = "m", ApiKey = "local" } },
            };
            using var h = new ResolverHarness(options);
            Assert.Throws<InvalidOperationException>(() => h.Resolver.Resolve(ModelSlot.Seed));
        }

        [Fact]
        public async Task All_local_active_resolves_to_the_rig()
        {
            using var h = new ResolverHarness(ResolverHarness.RigOptions());
            await ProfileSeeder.SeedAsync(h.Db, ResolverHarness.RigOptions());

            var resolved = h.Resolver.Resolve(ModelSlot.Concept);
            Assert.Equal("http://rig", resolved.BaseUrl);
            Assert.Equal("Concept-model", resolved.Model);
        }

        [Fact]
        public async Task Activating_openai_concept_profile_returns_openai_slot_with_key_from_config()
        {
            using var h = new ResolverHarness(ResolverHarness.RigOptions(), openAiApiKey: "sk-secret");
            await ProfileSeeder.SeedAsync(h.Db, ResolverHarness.RigOptions());

            // Switch the active profile to "Local + OpenAI Concept".
            var all = h.Db.ConfigProfiles.ToList();
            foreach (var p in all) p.IsActive = p.Name == ProfileSeeder.LocalPlusOpenAiConcept;
            await h.Db.SaveChangesAsync();

            var concept = h.Resolver.Resolve(ModelSlot.Concept);
            Assert.True(string.IsNullOrEmpty(concept.BaseUrl)); // hosted OpenAI
            Assert.Equal(ProfileSeeder.DefaultOpenAiModel, concept.Model);
            Assert.Equal("sk-secret", concept.ApiKey); // filled from user-secrets, never stored in the DB

            // Other slots stay local under this profile.
            Assert.Equal("http://rig", h.Resolver.Resolve(ModelSlot.Extraction).BaseUrl);
        }

        [Fact]
        public async Task Resolve_with_explicit_profile_id_ignores_the_active_profile()
        {
            using var h = new ResolverHarness(ResolverHarness.RigOptions(), openAiApiKey: "sk-secret");
            await ProfileSeeder.SeedAsync(h.Db, ResolverHarness.RigOptions()); // "All Local" active

            var allOpenAi = h.Db.ConfigProfiles.Single(p => p.Name == ProfileSeeder.AllOpenAi);
            var concept = h.Resolver.Resolve(ModelSlot.Concept, allOpenAi.Id);

            Assert.True(string.IsNullOrEmpty(concept.BaseUrl)); // the chosen profile's OpenAI slot, not the active rig
            Assert.Equal("sk-secret", concept.ApiKey);
        }
    }
}
