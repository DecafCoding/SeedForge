using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SeedForge.Data;
using SeedForge.Features.Config;
using SeedForge.Services.Ai;

namespace SeedForge.UnitTests
{
    /// <summary>Proves SetActiveAsync keeps exactly one profile active (satisfying the filtered unique index).</summary>
    public sealed class ProfileServiceTests : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly DbContextOptions<ApplicationDbContext> _dbOptions;

        public ProfileServiceTests()
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
            foreach (var slot in Enum.GetValues<SeedForge.Domain.ModelSlot>())
            {
                options.Slots[slot.ToString()] =
                    new LlmOptions { BaseUrl = "http://rig", ApiKey = "local", Model = "Qwen" };
            }
            return options;
        }

        [Fact]
        public async Task SetActive_leaves_exactly_one_active_profile()
        {
            using (var db = NewDb()) await ProfileSeeder.SeedAsync(db, RigOptions());

            int targetId;
            using (var db = NewDb())
            {
                targetId = db.ConfigProfiles.Single(p => p.Name == ProfileSeeder.AllOpenAi).Id;
                await new ProfileService(db).SetActiveAsync(targetId);
            }

            using var read = NewDb();
            var active = Assert.Single(read.ConfigProfiles.Where(p => p.IsActive).ToList());
            Assert.Equal(targetId, active.Id);
            Assert.Equal(3, read.ConfigProfiles.Count());
        }

        [Fact]
        public async Task SetActive_throws_for_missing_profile()
        {
            using var db = NewDb();
            await Assert.ThrowsAsync<InvalidOperationException>(() => new ProfileService(db).SetActiveAsync(999));
        }

        public void Dispose() => _connection.Dispose();
    }
}
