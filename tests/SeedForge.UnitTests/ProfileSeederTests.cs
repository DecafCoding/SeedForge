using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SeedForge.Data;
using SeedForge.Domain;
using SeedForge.Features.Config;
using SeedForge.Services.Ai;

namespace SeedForge.UnitTests
{
    /// <summary>Proves the first-run seeder is idempotent and produces exactly three profiles with one active.</summary>
    public sealed class ProfileSeederTests : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly DbContextOptions<ApplicationDbContext> _dbOptions;

        public ProfileSeederTests()
        {
            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();
            _dbOptions = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
            using var db = NewDb();
            db.Database.EnsureCreated();
        }

        private ApplicationDbContext NewDb() => new(_dbOptions);

        private static AiOptions RigOptions()
        {
            var options = new AiOptions();
            foreach (var slot in Enum.GetValues<ModelSlot>())
            {
                options.Slots[slot.ToString()] =
                    new LlmOptions { BaseUrl = "http://rig", ApiKey = "local", Model = "Qwen" };
            }
            return options;
        }

        [Fact]
        public async Task Seeding_twice_leaves_exactly_three_profiles_with_one_active_all_local()
        {
            var rig = RigOptions();

            using (var db = NewDb()) await ProfileSeeder.SeedAsync(db, rig);
            using (var db = NewDb()) await ProfileSeeder.SeedAsync(db, rig); // second run is a no-op

            using var read = NewDb();
            var profiles = read.ConfigProfiles.ToList();
            Assert.Equal(3, profiles.Count);
            var active = Assert.Single(profiles, p => p.IsActive);
            Assert.Equal(ProfileSeeder.AllLocal, active.Name);
            Assert.Contains(profiles, p => p.Name == ProfileSeeder.LocalPlusOpenAiConcept);
            Assert.Contains(profiles, p => p.Name == ProfileSeeder.AllOpenAi);
        }

        [Fact]
        public async Task All_local_profile_mirrors_the_appsettings_rig()
        {
            using (var db = NewDb()) await ProfileSeeder.SeedAsync(db, RigOptions());

            using var read = NewDb();
            var allLocal = read.ConfigProfiles.Single(p => p.Name == ProfileSeeder.AllLocal);
            var slots = ProfileSlots.Deserialize(allLocal.SlotsJson);
            Assert.Equal("http://rig", slots[ModelSlot.Concept.ToString()].BaseUrl);
            Assert.Equal("Qwen", slots[ModelSlot.Concept.ToString()].Model);
        }

        [Fact]
        public async Task Openai_slots_carry_a_blank_key_so_no_secret_is_stored()
        {
            using (var db = NewDb()) await ProfileSeeder.SeedAsync(db, RigOptions());

            using var read = NewDb();
            var allOpenAi = read.ConfigProfiles.Single(p => p.Name == ProfileSeeder.AllOpenAi);
            var slots = ProfileSlots.Deserialize(allOpenAi.SlotsJson);
            Assert.All(slots.Values, s => Assert.True(string.IsNullOrEmpty(s.ApiKey)));
        }

        public void Dispose() => _connection.Dispose();
    }
}
