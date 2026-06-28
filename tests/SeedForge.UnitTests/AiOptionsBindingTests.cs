using Microsoft.Extensions.Configuration;
using SeedForge.Services.Ai;

namespace SeedForge.UnitTests
{
    /// <summary>Proves the "Ai" config section binds into five populated <see cref="LlmOptions"/> slots.</summary>
    public class AiOptionsBindingTests
    {
        private const string Json = """
        {
          "Ai": {
            "Slots": {
              "Seed":         { "BaseUrl": "http://192.168.50.102:8070", "ApiKey": "local", "Model": "Qwen3.6-27B", "TimeoutSeconds": 300 },
              "Extraction":   { "BaseUrl": "http://192.168.50.102:8070", "ApiKey": "local", "Model": "Qwen3.6-27B", "TimeoutSeconds": 300 },
              "Scoring":      { "BaseUrl": "http://192.168.50.102:8070", "ApiKey": "local", "Model": "Qwen3.6-27B", "TimeoutSeconds": 300 },
              "Concept":      { "BaseUrl": "http://192.168.50.102:8070", "ApiKey": "local", "Model": "Qwen3.6-27B", "TimeoutSeconds": 300 },
              "Conversation": { "BaseUrl": "http://192.168.50.102:8070", "ApiKey": "local", "Model": "Qwen3.6-27B", "TimeoutSeconds": 300 }
            }
          }
        }
        """;

        private static AiOptions Bind()
        {
            using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(Json));
            var config = new ConfigurationBuilder().AddJsonStream(stream).Build();
            var options = new AiOptions();
            config.GetSection("Ai").Bind(options);
            return options;
        }

        [Fact]
        public void Binds_five_slots_from_config()
        {
            var options = Bind();
            Assert.Equal(5, options.Slots.Count);
        }

        [Fact]
        public void Concept_slot_carries_reference_rig_base_url()
        {
            var options = Bind();
            Assert.Equal("http://192.168.50.102:8070", options.Slots["Concept"].BaseUrl);
            Assert.Equal("Qwen3.6-27B", options.Slots["Concept"].Model);
            Assert.Equal(300, options.Slots["Concept"].TimeoutSeconds);
        }
    }
}
