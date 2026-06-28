using System.Diagnostics;
using SeedForge.Domain;

namespace SeedForge.Services.Ai
{
    /// <summary>Outcome of a per-slot connectivity check.</summary>
    public sealed record ConnectionResult(bool Ok, long LatencyMs, string? Error);

    /// <summary>Fires a trivial completion against a slot's endpoint to verify connectivity, never throwing.</summary>
    public sealed class ConnectionTester(LlmOptionsResolver resolver, ILlmClient client)
    {
        /// <summary>Resolves the slot, sends a "ping", and reports success+latency or a structured error.</summary>
        public async Task<ConnectionResult> TestConnectionAsync(ModelSlot slot, CancellationToken ct = default)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var options = resolver.Resolve(slot);
                var context = new AiCallContext(Guid.NewGuid().ToString("N"), "Diagnostics", slot);
                var messages = new[] { new ChatMessage("user", "ping") };
                await client.CompleteAsync(options, messages, context, ct);
                stopwatch.Stop();
                return new ConnectionResult(true, stopwatch.ElapsedMilliseconds, null);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                return new ConnectionResult(false, stopwatch.ElapsedMilliseconds, ex.Message);
            }
        }
    }
}
