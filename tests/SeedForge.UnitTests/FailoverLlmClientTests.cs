using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SeedForge.Data;
using SeedForge.Domain;
using SeedForge.Features.Config;
using SeedForge.Services.Ai;

namespace SeedForge.UnitTests
{
    /// <summary>
    /// The failover decorator: a connectivity failure on the primary endpoint retries once against the configured
    /// fallback profile's slot; every other case (disabled, non-connectivity error, same endpoint) rethrows untouched.
    /// </summary>
    public sealed class FailoverLlmClientTests
    {
        private static readonly IReadOnlyList<ChatMessage> Messages = new[] { new ChatMessage("user", "hi") };
        private static readonly AiCallContext Context = new("corr", "Extraction", ModelSlot.Extraction);
        private static LlmOptions LocalPrimary() => new() { BaseUrl = "http://rig", ApiKey = "local", Model = "local" };

        // ---- Connectivity classifier ----

        [Fact]
        public void IsConnectivityFailure_true_for_5xx_transport_and_timeout()
        {
            var none = CancellationToken.None;
            Assert.True(FailoverLlmClient.IsConnectivityFailure(new LlmException("x", statusCode: 503), none));
            Assert.True(FailoverLlmClient.IsConnectivityFailure(new LlmException("x", inner: new HttpRequestException()), none));
            Assert.True(FailoverLlmClient.IsConnectivityFailure(new LlmException("x", inner: new TaskCanceledException()), none));
            Assert.True(FailoverLlmClient.IsConnectivityFailure(new TaskCanceledException(), none)); // HttpClient timeout
            Assert.True(FailoverLlmClient.IsConnectivityFailure(new TimeoutException(), none));
        }

        [Fact]
        public void IsConnectivityFailure_false_for_4xx_bad_response_and_caller_cancel()
        {
            Assert.False(FailoverLlmClient.IsConnectivityFailure(new LlmException("x", statusCode: 400), CancellationToken.None));
            Assert.False(FailoverLlmClient.IsConnectivityFailure(new LlmException("bad json"), CancellationToken.None)); // model answered
            Assert.False(FailoverLlmClient.IsConnectivityFailure(new InvalidOperationException(), CancellationToken.None));

            // A connectivity-shaped error is NOT a failover trigger when the caller cancelled.
            Assert.False(FailoverLlmClient.IsConnectivityFailure(
                new LlmException("x", inner: new HttpRequestException()), new CancellationToken(canceled: true)));
        }

        // ---- Decorator behavior ----

        [Fact]
        public async Task Retries_on_fallback_when_enabled_and_endpoint_unreachable()
        {
            using var host = TestHost.Create(failoverEnabled: true, withFallbackProfile: true);
            var inner = new ScriptedInner(new LlmException("down", inner: new HttpRequestException()), null); // fail, then succeed
            var client = host.Client(inner);

            var result = await client.CompleteAsync(LocalPrimary(), Messages, Context);

            Assert.Equal("https://api.openai.com/v1/", result);    // echoes the endpoint actually used
            Assert.Equal(2, inner.Calls.Count);
            Assert.Equal("http://rig", inner.Calls[0].BaseUrl);     // primary first
            Assert.Equal("https://api.openai.com/v1/", inner.Calls[1].BaseUrl); // then the fallback profile's slot
        }

        [Fact]
        public async Task Rethrows_without_retry_when_failover_disabled()
        {
            using var host = TestHost.Create(failoverEnabled: false, withFallbackProfile: true);
            var inner = new ScriptedInner(new LlmException("down", inner: new HttpRequestException()), null);
            var client = host.Client(inner);

            await Assert.ThrowsAsync<LlmException>(() => client.CompleteAsync(LocalPrimary(), Messages, Context));
            Assert.Single(inner.Calls); // no fallback attempt
        }

        [Fact]
        public async Task Rethrows_without_retry_on_a_non_connectivity_error()
        {
            using var host = TestHost.Create(failoverEnabled: true, withFallbackProfile: true);
            var inner = new ScriptedInner(new LlmException("bad json"), null); // model answered — not a failover case
            var client = host.Client(inner);

            await Assert.ThrowsAsync<LlmException>(() => client.CompleteAsync(LocalPrimary(), Messages, Context));
            Assert.Single(inner.Calls);
        }

        [Fact]
        public async Task Rethrows_without_retry_when_fallback_is_the_same_endpoint()
        {
            // Fallback profile slot points at the SAME place as the primary ⇒ retrying there is pointless.
            using var host = TestHost.Create(failoverEnabled: true, withFallbackProfile: true,
                fallbackSlot: new LlmOptions { BaseUrl = "http://rig", Model = "local" });
            var inner = new ScriptedInner(new LlmException("down", inner: new HttpRequestException()), null);
            var client = host.Client(inner);

            await Assert.ThrowsAsync<LlmException>(() => client.CompleteAsync(LocalPrimary(), Messages, Context));
            Assert.Single(inner.Calls);
        }

        [Fact]
        public async Task FailoverSettingsService_round_trips_enabled_and_profile()
        {
            using var host = TestHost.Create(failoverEnabled: false, withFallbackProfile: false);
            using var scope = host.Provider.CreateScope();
            var svc = new FailoverSettingsService(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>());

            var initial = await svc.GetAsync();
            Assert.False(initial.Enabled);
            Assert.Null(initial.FallbackProfileId);

            await svc.UpdateAsync(enabled: true, fallbackProfileId: 7);
            var updated = await svc.GetAsync();
            Assert.True(updated.Enabled);
            Assert.Equal(7, updated.FallbackProfileId);
        }

        // ---- Test scaffolding ----

        /// <summary>An inner client that runs a fixed script of outcomes (an exception to throw, or null to succeed by echoing the endpoint).</summary>
        private sealed class ScriptedInner(params Exception?[] script) : ILlmClient
        {
            private readonly Queue<Exception?> _script = new(script);
            public List<LlmOptions> Calls { get; } = new();

            public Task<string> CompleteAsync(LlmOptions o, IReadOnlyList<ChatMessage> m, AiCallContext c, CancellationToken ct = default)
            {
                Calls.Add(o);
                var ex = _script.Count > 0 ? _script.Dequeue() : null;
                return ex is not null ? Task.FromException<string>(ex) : Task.FromResult(o.BaseUrl);
            }

            public Task<T> CompleteStructuredAsync<T>(LlmOptions o, IReadOnlyList<ChatMessage> m, AiCallContext c, CancellationToken ct = default)
                => throw new NotSupportedException();
        }

        private sealed class TestHost : IDisposable
        {
            private readonly SqliteConnection _connection;
            public ServiceProvider Provider { get; }

            private TestHost(SqliteConnection connection, ServiceProvider provider)
            {
                _connection = connection;
                Provider = provider;
            }

            public FailoverLlmClient Client(ILlmClient inner) =>
                new(inner, Provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<FailoverLlmClient>.Instance);

            public static TestHost Create(bool failoverEnabled, bool withFallbackProfile, LlmOptions? fallbackSlot = null)
            {
                var connection = new SqliteConnection("DataSource=:memory:");
                connection.Open();

                var services = new ServiceCollection();
                services.AddDbContext<ApplicationDbContext>(o => o.UseSqlite(connection));
                services.AddSingleton<IOptionsMonitor<AiOptions>>(new TestOptionsMonitor<AiOptions>(new AiOptions()));
                services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
                services.AddScoped<LlmOptionsResolver>();
                var provider = services.BuildServiceProvider();

                using (var scope = provider.CreateScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    db.Database.EnsureCreated();

                    int? fallbackProfileId = null;
                    if (withFallbackProfile)
                    {
                        var slot = fallbackSlot ?? new LlmOptions { BaseUrl = "https://api.openai.com/v1/", ApiKey = "", Model = "gpt-4o" };
                        var profile = new ConfigProfile
                        {
                            Name = "Fallback",
                            SlotsJson = ProfileSlots.Serialize(new Dictionary<string, LlmOptions> { ["Extraction"] = slot }),
                            CreatedAtUtc = DateTime.UtcNow,
                        };
                        db.ConfigProfiles.Add(profile);
                        db.SaveChanges();
                        fallbackProfileId = profile.Id;
                    }

                    db.FailoverSettings.Add(new FailoverSetting { Id = 1, Enabled = failoverEnabled, FallbackProfileId = fallbackProfileId });
                    db.SaveChanges();
                }

                return new TestHost(connection, provider);
            }

            public void Dispose()
            {
                Provider.Dispose();
                _connection.Dispose();
            }
        }
    }
}
