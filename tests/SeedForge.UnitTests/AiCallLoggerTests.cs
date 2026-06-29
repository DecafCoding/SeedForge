using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SeedForge.Data;
using SeedForge.Domain;
using SeedForge.Services.Ai;

namespace SeedForge.UnitTests
{
    /// <summary>Proves the decorator writes exactly one AiCallLog per call (success + failure) via a fresh DB scope and rethrows model failures.</summary>
    public class AiCallLoggerTests : IDisposable
    {
        public sealed record Foo(string Title, int Count);

        private readonly SqliteConnection _connection;
        private readonly ServiceProvider _provider;

        public AiCallLoggerTests()
        {
            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddDbContext<ApplicationDbContext>(o => o.UseSqlite(_connection));
            _provider = services.BuildServiceProvider();

            using var scope = _provider.CreateScope();
            scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.EnsureCreated();
        }

        private AiCallLogger BuildLogger(StubHttpMessageHandler handler) => new(
            new LlmClient(() => handler),
            _provider.GetRequiredService<IServiceScopeFactory>(),
            new CostEstimator(),
            _provider.GetRequiredService<ILogger<AiCallLogger>>());

        private List<AiCallLog> Logs()
        {
            using var scope = _provider.CreateScope();
            return scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().AiCallLogs.ToList();
        }

        private static LlmOptions Options() => new() { BaseUrl = "http://rig:8070", ApiKey = "local", Model = "m" };

        private static IReadOnlyList<ChatMessage> Messages() =>
            new[] { new ChatMessage("system", "sys"), new ChatMessage("user", "usr") };

        [Fact]
        public async Task Successful_call_writes_one_log_with_correlation_and_latency()
        {
            var handler = new StubHttpMessageHandler(StubHttpMessageHandler.ChatResponse("{\"title\":\"x\",\"count\":1}"));
            var logger = BuildLogger(handler);
            var ctx = new AiCallContext("corr-success", "Concept", ModelSlot.Concept);

            var result = await logger.CompleteStructuredAsync<Foo>(Options(), Messages(), ctx);

            Assert.Equal("x", result.Title);
            var logs = Logs();
            var log = Assert.Single(logs);
            Assert.Equal("corr-success", log.CorrelationId);
            Assert.True(log.Success);
            Assert.Equal(11, log.PromptTokens);
            Assert.Equal("sys", log.SystemMessage);
            Assert.Equal("usr", log.UserMessage);
            Assert.True(log.LatencyMs >= 0);
        }

        [Fact]
        public async Task Failed_call_writes_one_failure_log_and_rethrows()
        {
            var handler = new StubHttpMessageHandler("{\"error\":\"boom\"}", HttpStatusCode.InternalServerError);
            var logger = BuildLogger(handler);
            var ctx = new AiCallContext("corr-fail", "Concept", ModelSlot.Concept);

            await Assert.ThrowsAsync<LlmException>(() => logger.CompleteStructuredAsync<Foo>(Options(), Messages(), ctx));

            var log = Assert.Single(Logs());
            Assert.False(log.Success);
            Assert.False(string.IsNullOrEmpty(log.ErrorMessage));
            Assert.Equal("corr-fail", log.CorrelationId);
        }

        public void Dispose()
        {
            _provider.Dispose();
            _connection.Dispose();
        }
    }

    /// <summary>Proves the DI composition resolves ILlmClient to the failover decorator (over the logger) with scope validation on.</summary>
    public class CompositionTests
    {
        [Fact]
        public void ILlmClient_resolves_to_FailoverLlmClient_over_AiCallLogger()
        {
            var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
            var services = new ServiceCollection();
            services.AddLogging();
            // The scoped resolver reads IConfiguration + ApplicationDbContext; register both as the real app does.
            services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(config);
            services.AddDbContext<ApplicationDbContext>(o => o.UseSqlite("DataSource=:memory:"));
            services.AddAiServices(config);

            using var provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateScopes = true,
                ValidateOnBuild = true,
            });

            var client = provider.GetRequiredService<ILlmClient>();
            Assert.IsType<FailoverLlmClient>(client);                 // outermost decorator
            Assert.IsType<AiCallLogger>(provider.GetRequiredService<AiCallLogger>()); // still registered as the inner
        }
    }
}
