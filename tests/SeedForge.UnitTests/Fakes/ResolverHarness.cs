using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SeedForge.Data;
using SeedForge.Domain;
using SeedForge.Services.Ai;

namespace SeedForge.UnitTests.Fakes
{
    /// <summary>
    /// Scaffolding for <see cref="LlmOptionsResolver"/> tests: a SQLite-in-memory <see cref="ApplicationDbContext"/>
    /// (to seed/add <c>ConfigProfile</c> rows), the bound appsettings <see cref="AiOptions"/> fallback, and an
    /// optional <c>Ai:OpenAiApiKey</c> for the secret-substitution path.
    /// </summary>
    public sealed class ResolverHarness : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly DbContextOptions<ApplicationDbContext> _dbOptions;

        public ResolverHarness(AiOptions appsettings, string? openAiApiKey = null)
        {
            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();
            _dbOptions = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
            Db = new ApplicationDbContext(_dbOptions);
            Db.Database.EnsureCreated();

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Ai:OpenAiApiKey"] = openAiApiKey })
                .Build();

            Resolver = new LlmOptionsResolver(Db, new TestOptionsMonitor<AiOptions>(appsettings), config);
        }

        /// <summary>The context backing the resolver — add/seed <c>ConfigProfile</c> rows here, then resolve.</summary>
        public ApplicationDbContext Db { get; }

        public LlmOptionsResolver Resolver { get; }

        /// <summary>Builds an <see cref="AiOptions"/> with every slot pointing at the local rig.</summary>
        public static AiOptions RigOptions()
        {
            var options = new AiOptions();
            foreach (var slot in Enum.GetValues<ModelSlot>())
            {
                options.Slots[slot.ToString()] =
                    new LlmOptions { BaseUrl = "http://rig", ApiKey = "local", Model = $"{slot}-model" };
            }
            return options;
        }

        public void Dispose()
        {
            Db.Dispose();
            _connection.Dispose();
        }
    }
}
