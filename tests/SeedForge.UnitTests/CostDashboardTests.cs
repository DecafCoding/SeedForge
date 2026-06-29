using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SeedForge.Data;
using SeedForge.Domain;
using SeedForge.Features.Observability;

namespace SeedForge.UnitTests
{
    /// <summary>
    /// Aggregation tests for <see cref="CostDashboard"/> over seeded <see cref="AiCallLog"/>/<see cref="Transcript"/>
    /// rows in SQLite-in-memory: per-stage and per-provider token sums, the Apify-units sum, and the date filter.
    /// </summary>
    public sealed class CostDashboardTests : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly DbContextOptions<ApplicationDbContext> _dbOptions;
        private static readonly DateTime Now = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        public CostDashboardTests()
        {
            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();
            _dbOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(_connection).Options;
            using var db = NewDb();
            db.Database.EnsureCreated();
        }

        private ApplicationDbContext NewDb() => new(_dbOptions);

        private static AiCallLog Log(string stage, bool isLocal, string model, int prompt, int completion, double cost, DateTime when) =>
            new()
            {
                CorrelationId = "c",
                Stage = stage,
                Slot = ModelSlot.Concept,
                IsLocal = isLocal,
                BaseUrl = "http://rig",
                Model = model,
                PromptTokens = prompt,
                CompletionTokens = completion,
                TotalTokens = prompt + completion,
                EstimatedCost = cost,
                Success = true,
                CreatedAtUtc = when,
            };

        private void Seed(params AiCallLog[] logs)
        {
            using var db = NewDb();
            db.AiCallLogs.AddRange(logs);
            db.SaveChanges();
        }

        [Fact]
        public async Task PerStageAsync_sums_tokens_and_counts_per_stage()
        {
            Seed(
                Log("Scoring", isLocal: true, "qwen", 100, 20, 0, Now),
                Log("Scoring", isLocal: true, "qwen", 50, 10, 0, Now),
                Log("Concept", isLocal: false, "gpt-4o", 200, 80, 0.5, Now));

            using var db = NewDb();
            var perStage = await new CostDashboard(db).PerStageAsync(Now.AddDays(-1));

            var scoring = perStage.Single(s => s.Stage == "Scoring");
            Assert.Equal(2, scoring.Calls);
            Assert.Equal(150, scoring.PromptTokens);
            Assert.Equal(30, scoring.CompletionTokens);
            Assert.Equal(180, scoring.TotalTokens);
            Assert.Equal(0, scoring.EstimatedCost);

            var concept = perStage.Single(s => s.Stage == "Concept");
            Assert.Equal(1, concept.Calls);
            Assert.Equal(280, concept.TotalTokens);
            Assert.Equal(0.5, concept.EstimatedCost);
        }

        [Fact]
        public async Task PerProviderAsync_separates_local_from_hosted()
        {
            Seed(
                Log("Scoring", isLocal: true, "qwen", 100, 20, 0, Now),
                Log("Concept", isLocal: false, "gpt-4o", 200, 80, 1.25, Now));

            using var db = NewDb();
            var perProvider = await new CostDashboard(db).PerProviderAsync(Now.AddDays(-1));

            var local = perProvider.Single(p => p.Provider == "local");
            Assert.True(local.IsLocal);
            Assert.Equal(120, local.TotalTokens);
            Assert.Equal(0, local.EstimatedCost);

            var hosted = perProvider.Single(p => p.Provider == "gpt-4o");
            Assert.False(hosted.IsLocal);
            Assert.Equal(280, hosted.TotalTokens);
            Assert.Equal(1.25, hosted.EstimatedCost);
        }

        [Fact]
        public async Task TotalsAsync_sums_every_call()
        {
            Seed(
                Log("Scoring", isLocal: true, "qwen", 100, 20, 0, Now),
                Log("Concept", isLocal: false, "gpt-4o", 200, 80, 1.25, Now));

            using var db = NewDb();
            var totals = await new CostDashboard(db).TotalsAsync(Now.AddDays(-1));

            Assert.Equal(2, totals.Calls);
            Assert.Equal(300, totals.PromptTokens);
            Assert.Equal(100, totals.CompletionTokens);
            Assert.Equal(400, totals.TotalTokens);
            Assert.Equal(1.25, totals.EstimatedCost);
        }

        [Fact]
        public async Task ApifyCostAsync_sums_transcript_units_ignoring_nulls()
        {
            using (var db = NewDb())
            {
                db.Transcripts.AddRange(
                    new Transcript { PlainText = "a", ApifyCostUnits = 0.4, CreatedAtUtc = Now },
                    new Transcript { PlainText = "b", ApifyCostUnits = 0.6, CreatedAtUtc = Now },
                    new Transcript { PlainText = "c", ApifyCostUnits = null, CreatedAtUtc = Now });
                db.SaveChanges();
            }

            using var read = NewDb();
            var apify = await new CostDashboard(read).ApifyCostAsync(Now.AddDays(-1));
            Assert.Equal(1.0, apify, precision: 6);
        }

        [Fact]
        public async Task FromUtc_filter_excludes_older_rows()
        {
            Seed(
                Log("Scoring", isLocal: true, "qwen", 100, 20, 0, Now),
                Log("Scoring", isLocal: true, "qwen", 999, 999, 0, Now.AddDays(-40)));

            using var db = NewDb();
            var perStage = await new CostDashboard(db).PerStageAsync(Now.AddDays(-30));

            var scoring = perStage.Single(s => s.Stage == "Scoring");
            Assert.Equal(1, scoring.Calls);
            Assert.Equal(100, scoring.PromptTokens);
        }

        public void Dispose() => _connection.Dispose();
    }
}
