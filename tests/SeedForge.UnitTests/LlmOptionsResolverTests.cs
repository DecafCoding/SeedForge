using SeedForge.Domain;
using SeedForge.Services.Ai;

namespace SeedForge.UnitTests
{
    /// <summary>Proves the resolver returns a per-slot copy and rejects unconfigured slots.</summary>
    public class LlmOptionsResolverTests
    {
        private static LlmOptionsResolver BuildResolver()
        {
            var options = new AiOptions
            {
                Slots =
                {
                    ["Extraction"] = new LlmOptions { BaseUrl = "http://rig", Model = "extract-model", ApiKey = "local" },
                    ["Concept"] = new LlmOptions { BaseUrl = "http://rig", Model = "concept-model", ApiKey = "local" },
                },
            };
            return new LlmOptionsResolver(new TestOptionsMonitor<AiOptions>(options));
        }

        [Fact]
        public void Resolve_returns_the_slots_model()
        {
            var resolver = BuildResolver();
            Assert.Equal("extract-model", resolver.Resolve(ModelSlot.Extraction).Model);
        }

        [Fact]
        public void Resolve_returns_a_copy_not_the_shared_instance()
        {
            var resolver = BuildResolver();
            var first = resolver.Resolve(ModelSlot.Concept);
            first.Temperature = 0.9;
            var second = resolver.Resolve(ModelSlot.Concept);
            Assert.Null(second.Temperature);
        }

        [Fact]
        public void Resolve_throws_for_unconfigured_slot()
        {
            var resolver = BuildResolver();
            Assert.Throws<InvalidOperationException>(() => resolver.Resolve(ModelSlot.Seed));
        }
    }
}
