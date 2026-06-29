using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SeedForge.Data;
using SeedForge.Domain;
using SeedForge.Features.Observability;
using SeedForge.Services.Ai;

namespace SeedForge.UnitTests
{
    /// <summary>Proves replay reissues a stored call against a different config, returns both outputs, and writes a new log without mutating the original.</summary>
    public sealed class ReplayCallTests : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly DbContextOptions<ApplicationDbContext> _dbOptions;
        private readonly ServiceProvider _provider;

        public ReplayCallTests()
        {
            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();
            _dbOptions = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddDbContext<ApplicationDbContext>(o => o.UseSqlite(_connection));
            _provider = services.BuildServiceProvider();

            using var db = NewDb();
            db.Database.EnsureCreated();
        }

        private ApplicationDbContext NewDb() => new(_dbOptions);

        private LlmOptionsResolver Resolver()
        {
            var options = new AiOptions
            {
                Slots = { ["Concept"] = new LlmOptions { BaseUrl = "http://rig:8070", ApiKey = "local", Model = "new-model" } },
            };
            return new LlmOptionsResolver(NewDb(), new TestOptionsMonitor<AiOptions>(options), new ConfigurationBuilder().Build());
        }

        /// <summary>The decorated client: writes one AiCallLog per call via a fresh scope, like the real app.</summary>
        private ILlmClient DecoratedClient(StubHttpMessageHandler handler) => new AiCallLogger(
            new LlmClient(() => handler),
            _provider.GetRequiredService<IServiceScopeFactory>(),
            new CostEstimator(),
            _provider.GetRequiredService<ILogger<AiCallLogger>>());

        [Fact]
        public async Task Replay_returns_both_outputs_and_writes_a_new_log_leaving_original_untouched()
        {
            int originalId;
            using (var db = NewDb())
            {
                var original = new AiCallLog
                {
                    CorrelationId = "corr-orig",
                    Stage = "ConceptBuilder",
                    Slot = ModelSlot.Concept,
                    Model = "orig-model",
                    SystemMessage = "sys",
                    UserMessage = "usr",
                    RawResponse = "ORIGINAL",
                    Success = true,
                    CreatedAtUtc = DateTime.UtcNow,
                };
                db.AiCallLogs.Add(original);
                db.SaveChanges();
                originalId = original.Id;
            }

            var handler = new StubHttpMessageHandler(StubHttpMessageHandler.ChatResponse("REPLAYED"));
            ReplayResult result;
            using (var db = NewDb())
            {
                var replay = new ReplayCallHandler(db, DecoratedClient(handler), Resolver());
                result = await replay.HandleAsync(new(originalId, ModelSlot.Concept), default);
            }

            Assert.Equal("orig-model", result.OriginalModel);
            Assert.Equal("ORIGINAL", result.OriginalResponse);
            Assert.Equal("new-model", result.NewModel);
            Assert.Equal("REPLAYED", result.NewResponse);

            using var read = NewDb();
            var originalAfter = read.AiCallLogs.Single(l => l.Id == originalId);
            Assert.Equal("ORIGINAL", originalAfter.RawResponse); // untouched
            Assert.Equal("orig-model", originalAfter.Model);

            // A new log row was written for the replay, attributed back to the original.
            var replayLog = Assert.Single(read.AiCallLogs.Where(l => l.Stage == "Replay").ToList());
            Assert.Equal(nameof(AiCallLog), replayLog.RelatedEntityType);
            Assert.Equal(originalId, replayLog.RelatedEntityId);
            Assert.Equal(2, read.AiCallLogs.Count());
        }

        public void Dispose()
        {
            _provider.Dispose();
            _connection.Dispose();
        }
    }
}
