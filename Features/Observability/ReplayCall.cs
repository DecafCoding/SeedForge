using SeedForge.Data;
using SeedForge.Domain;
using SeedForge.Services.Ai;

namespace SeedForge.Features.Observability
{
    /// <param name="AiCallLogId">The stored call whose messages are reissued.</param>
    /// <param name="Slot">The slot to resolve the target options from.</param>
    /// <param name="ProfileId">Optional profile to resolve against; null ⇒ active profile.</param>
    public sealed record ReplayCallRequest(int AiCallLogId, ModelSlot Slot, int? ProfileId = null);

    /// <summary>The side-by-side A/B comparison: the original stored output and the freshly replayed one.</summary>
    public sealed record ReplayResult(string OriginalModel, string OriginalResponse, string NewModel, string NewResponse);

    /// <summary>
    /// Reissues a stored <see cref="AiCallLog"/>'s system/user messages against a chosen profile/slot for a raw-text
    /// A/B. The original log is immutable; the replay is itself logged (the decorator writes a NEW <see cref="AiCallLog"/>
    /// linked back to the original via <c>RelatedEntityId</c>). Reissued as free-text — the original structured type is
    /// not reconstructed.
    /// </summary>
    public sealed class ReplayCallHandler(ApplicationDbContext db, ILlmClient llm, LlmOptionsResolver slots)
    {
        public async Task<ReplayResult> HandleAsync(ReplayCallRequest req, CancellationToken ct)
        {
            var original = await db.AiCallLogs.FindAsync([req.AiCallLogId], ct)
                ?? throw new InvalidOperationException($"AiCallLog {req.AiCallLogId} not found");

            var options = slots.Resolve(req.Slot, req.ProfileId);

            // A fresh correlation id; the replay is attributed back to the original log it A/B's against.
            var ctx = new AiCallContext(
                Guid.NewGuid().ToString("N"), "Replay", req.Slot,
                nameof(AiCallLog), original.Id);

            var messages = new List<ChatMessage>();
            if (!string.IsNullOrEmpty(original.SystemMessage)) messages.Add(new ChatMessage("system", original.SystemMessage));
            messages.Add(new ChatMessage("user", original.UserMessage));

            var newResponse = await llm.CompleteAsync(options, messages, ctx, ct);

            return new ReplayResult(original.Model, original.RawResponse, options.Model, newResponse);
        }
    }
}
