using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SeedForge.Data;
using SeedForge.Domain;

namespace SeedForge.Services.Ai
{
    /// <summary>
    /// Outermost <see cref="ILlmClient"/> decorator: when a call fails because the endpoint is unreachable
    /// (connection refused / timeout / HTTP 5xx), it retries that one call <strong>once</strong> against the configured
    /// fallback profile's matching slot. Disabled by default — no hosted-provider spend without an explicit opt-in on
    /// <c>/config</c>. The retry flows back through the inner decorator chain, so it gets its own <see cref="AiCallLog"/>
    /// row (the failed primary attempt and the successful fallback attempt both appear in the trace).
    /// <para>
    /// Singleton: it opens a fresh DI scope per failover (<see cref="IServiceScopeFactory"/>) to read the
    /// <see cref="FailoverSetting"/> and resolve the fallback options, avoiding the singleton→scoped captive-dependency
    /// hazard. It reads the setting directly via <see cref="ApplicationDbContext"/> rather than a Features service so the
    /// Services layer keeps no dependency on Features.
    /// </para>
    /// </summary>
    public sealed class FailoverLlmClient(
        ILlmClient inner,
        IServiceScopeFactory scopeFactory,
        ILogger<FailoverLlmClient> logger) : ILlmClient
    {
        public Task<string> CompleteAsync(
            LlmOptions options, IReadOnlyList<ChatMessage> messages, AiCallContext context, CancellationToken ct = default)
            => ExecuteAsync(options, context, ct, o => inner.CompleteAsync(o, messages, context, ct));

        public Task<T> CompleteStructuredAsync<T>(
            LlmOptions options, IReadOnlyList<ChatMessage> messages, AiCallContext context, CancellationToken ct = default)
            => ExecuteAsync(options, context, ct, o => inner.CompleteStructuredAsync<T>(o, messages, context, ct));

        private async Task<TResult> ExecuteAsync<TResult>(
            LlmOptions primary, AiCallContext context, CancellationToken ct, Func<LlmOptions, Task<TResult>> call)
        {
            try
            {
                return await call(primary);
            }
            catch (Exception ex) when (IsConnectivityFailure(ex, ct))
            {
                var fallback = await ResolveFallbackAsync(context.Slot, ct);
                if (fallback is null || SameEndpoint(primary, fallback))
                {
                    // No fallback configured, or it points at the same endpoint — surface the original failure so the
                    // caller's existing retry/backoff still applies.
                    throw;
                }

                logger.LogWarning(ex,
                    "Slot {Slot} failover: primary endpoint {Url} unavailable → retrying on the fallback profile",
                    context.Slot, DisplayUrl(primary.BaseUrl));

                return await call(fallback); // one retry; its own AiCallLog row is written by the inner logger
            }
        }

        /// <summary>The fallback slot options when failover is enabled and configured, else null.</summary>
        private async Task<LlmOptions?> ResolveFallbackAsync(ModelSlot slot, CancellationToken ct)
        {
            using var scope = scopeFactory.CreateScope();
            var sp = scope.ServiceProvider;
            var db = sp.GetRequiredService<ApplicationDbContext>();

            var setting = await db.FailoverSettings.FirstOrDefaultAsync(s => s.Id == 1, ct);
            if (setting is null || !setting.Enabled || setting.FallbackProfileId is not int profileId)
            {
                return null;
            }

            // A dangling fallback id (profile deleted) means "no failover" — don't silently fall through to appsettings.
            if (!await db.ConfigProfiles.AnyAsync(p => p.Id == profileId, ct))
            {
                return null;
            }

            try
            {
                return sp.GetRequiredService<LlmOptionsResolver>().Resolve(slot, profileId);
            }
            catch (InvalidOperationException)
            {
                // The fallback profile has no options for this slot and appsettings doesn't either — nothing to fail to.
                return null;
            }
        }

        /// <summary>
        /// Whether <paramref name="ex"/> means the endpoint was unreachable/unavailable (worth a failover). A
        /// caller-initiated cancellation is never treated as a connectivity failure.
        /// </summary>
        internal static bool IsConnectivityFailure(Exception ex, CancellationToken ct)
        {
            if (ct.IsCancellationRequested)
            {
                return false; // the caller cancelled — not an endpoint problem
            }

            return ex switch
            {
                LlmException le => le.IsConnectivityFailure,
                // An HttpClient timeout surfaces as a TaskCanceledException that is NOT tied to the caller's token.
                TaskCanceledException => true,
                TimeoutException => true,
                _ => false,
            };
        }

        /// <summary>Two slots target the same place when their base URL and model match — no point retrying there.</summary>
        private static bool SameEndpoint(LlmOptions a, LlmOptions b) =>
            string.Equals(a.BaseUrl, b.BaseUrl, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.Model, b.Model, StringComparison.Ordinal);

        private static string DisplayUrl(string baseUrl) =>
            string.IsNullOrWhiteSpace(baseUrl) ? "OpenAI (hosted)" : baseUrl;
    }
}
