using SeedForge.Services.Ai;

namespace SeedForge.UnitTests.Fakes
{
    /// <summary>
    /// In-memory <see cref="ILlmClient"/> for deterministic slice tests. Canned structured responses are
    /// keyed by their CLR type; free-text responses are dequeued in call order. Every received
    /// <see cref="AiCallContext"/> is recorded so tests can assert correlation-id threading.
    /// </summary>
    public sealed class FakeLlmClient : ILlmClient
    {
        private readonly Dictionary<Type, object> _structured = new();
        private readonly Queue<string> _freeText = new();

        /// <summary>The contexts received, in call order — for asserting correlation id / stage / slot.</summary>
        public List<AiCallContext> Contexts { get; } = new();

        /// <summary>Registers (or replaces) the canned structured response returned for type <typeparamref name="T"/>.</summary>
        public FakeLlmClient SetStructured<T>(T response) where T : notnull
        {
            _structured[typeof(T)] = response;
            return this;
        }

        /// <summary>Enqueues a canned free-text response, returned in call order by <see cref="CompleteAsync"/>.</summary>
        public FakeLlmClient EnqueueText(string response)
        {
            _freeText.Enqueue(response);
            return this;
        }

        public Task<string> CompleteAsync(
            LlmOptions options, IReadOnlyList<ChatMessage> messages, AiCallContext context, CancellationToken ct = default)
        {
            Contexts.Add(context);
            if (_freeText.Count == 0)
            {
                throw new InvalidOperationException("FakeLlmClient: no free-text response enqueued.");
            }
            return Task.FromResult(_freeText.Dequeue());
        }

        public Task<T> CompleteStructuredAsync<T>(
            LlmOptions options, IReadOnlyList<ChatMessage> messages, AiCallContext context, CancellationToken ct = default)
        {
            Contexts.Add(context);
            if (!_structured.TryGetValue(typeof(T), out var response))
            {
                throw new InvalidOperationException($"FakeLlmClient: no structured response configured for {typeof(T).Name}.");
            }
            return Task.FromResult((T)response);
        }
    }
}
