using SeedForge.Domain;
using SeedForge.Services.Ai;
using SeedForge.UnitTests.Fakes;

namespace SeedForge.UnitTests
{
    /// <summary>Proves the test fake returns its configured structured object and records received contexts.</summary>
    public class FakeLlmClientTests
    {
        private sealed record Probe(string Value, int Count);

        private static LlmOptions Options() => new() { BaseUrl = "http://rig", ApiKey = "local", Model = "m" };

        private static IReadOnlyList<ChatMessage> Messages() =>
            new[] { new ChatMessage("system", "sys"), new ChatMessage("user", "usr") };

        [Fact]
        public async Task Returns_configured_structured_object_and_records_context()
        {
            var fake = new FakeLlmClient().SetStructured(new Probe("hello", 3));
            var ctx = new AiCallContext("corr-1", "Probe", ModelSlot.Seed, nameof(Transcript), 7);

            var result = await fake.CompleteStructuredAsync<Probe>(Options(), Messages(), ctx);

            Assert.Equal("hello", result.Value);
            Assert.Equal(3, result.Count);
            var recorded = Assert.Single(fake.Contexts);
            Assert.Equal("corr-1", recorded.CorrelationId);
            Assert.Equal(ModelSlot.Seed, recorded.Slot);
        }
    }
}
