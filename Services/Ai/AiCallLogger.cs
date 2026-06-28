using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using SeedForge.Data;
using SeedForge.Domain;

namespace SeedForge.Services.Ai
{
    /// <summary>
    /// Decorates <see cref="LlmClient"/> so exactly one <see cref="AiCallLog"/> is written per call (success or failure).
    /// As a singleton it opens a fresh DB scope per write via <see cref="IServiceScopeFactory"/> to avoid the
    /// singleton→scoped captive-dependency hazard. A model-call failure is rethrown after its row is written;
    /// a log-write failure is swallowed (logged) so it never masks a successful model result.
    /// </summary>
    public sealed class AiCallLogger(
        LlmClient inner,
        IServiceScopeFactory scopeFactory,
        CostEstimator costEstimator,
        ILogger<AiCallLogger> logger) : ILlmClient
    {
        public Task<string> CompleteAsync(
            LlmOptions options, IReadOnlyList<ChatMessage> messages, AiCallContext context, CancellationToken ct = default)
            => ExecuteAsync(options, messages, context, ct, () => inner.CompleteRawAsync(options, messages, context, ct));

        public Task<T> CompleteStructuredAsync<T>(
            LlmOptions options, IReadOnlyList<ChatMessage> messages, AiCallContext context, CancellationToken ct = default)
            => ExecuteAsync(options, messages, context, ct, () => inner.CompleteStructuredRawAsync<T>(options, messages, context, ct));

        private async Task<T> ExecuteAsync<T>(
            LlmOptions options, IReadOnlyList<ChatMessage> messages, AiCallContext context, CancellationToken ct,
            Func<Task<LlmResult<T>>> call)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var result = await call();
                stopwatch.Stop();
                var log = BuildLog(options, messages, context, stopwatch.ElapsedMilliseconds,
                    result.RawResponse, result.Usage, success: true, errorMessage: null);
                await WriteAsync(log, ct);
                return result.Value;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                var rawBody = (ex as LlmException)?.ResponseBody ?? string.Empty;
                var log = BuildLog(options, messages, context, stopwatch.ElapsedMilliseconds,
                    rawBody, usage: null, success: false, errorMessage: ex.Message);
                await WriteAsync(log, ct);
                throw;
            }
        }

        private AiCallLog BuildLog(
            LlmOptions options, IReadOnlyList<ChatMessage> messages, AiCallContext context,
            long latencyMs, string rawResponse, Usage? usage, bool success, string? errorMessage)
        {
            var promptTokens = usage?.PromptTokens ?? 0;
            var completionTokens = usage?.CompletionTokens ?? 0;

            return new AiCallLog
            {
                CorrelationId = context.CorrelationId,
                Stage = context.Stage,
                Slot = context.Slot,
                IsLocal = costEstimator.IsLocal(options),
                BaseUrl = options.BaseUrl,
                Model = options.Model,
                SystemMessage = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? string.Empty,
                UserMessage = messages.FirstOrDefault(m => m.Role == "user")?.Content ?? string.Empty,
                RawResponse = rawResponse,
                PromptTokens = promptTokens,
                CompletionTokens = completionTokens,
                TotalTokens = usage?.TotalTokens ?? (promptTokens + completionTokens),
                LatencyMs = latencyMs,
                EstimatedCost = costEstimator.Estimate(options, promptTokens, completionTokens),
                Temperature = options.Temperature,
                ReasoningEffort = options.ReasoningEffort,
                Success = success,
                ErrorMessage = errorMessage,
                RelatedEntityType = context.RelatedEntityType,
                RelatedEntityId = context.RelatedEntityId,
                CreatedAtUtc = DateTime.UtcNow,
            };
        }

        private async Task WriteAsync(AiCallLog log, CancellationToken ct)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                db.AiCallLogs.Add(log);
                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                // Never let a logging failure mask the model result.
                logger.LogError(ex, "Failed to persist AiCallLog for correlation {CorrelationId}", log.CorrelationId);
            }
        }
    }
}
