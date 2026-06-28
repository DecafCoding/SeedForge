using SeedForge.Services.Ai;

namespace SeedForge.UnitTests
{
    /// <summary>Proves local-vs-hosted detection and that local calls cost nothing.</summary>
    public class CostEstimatorTests
    {
        private readonly CostEstimator _estimator = new();

        [Fact]
        public void Reference_rig_is_local()
        {
            var options = new LlmOptions { BaseUrl = "http://192.168.50.102:8070" };
            Assert.True(_estimator.IsLocal(options));
        }

        [Fact]
        public void Blank_base_url_is_hosted()
        {
            var options = new LlmOptions { BaseUrl = "" };
            Assert.False(_estimator.IsLocal(options));
        }

        [Fact]
        public void Openai_host_is_hosted()
        {
            var options = new LlmOptions { BaseUrl = "https://api.openai.com/v1/" };
            Assert.False(_estimator.IsLocal(options));
        }

        [Fact]
        public void Estimate_is_zero_for_local()
        {
            var options = new LlmOptions { BaseUrl = "http://192.168.50.102:8070", Model = "Qwen3.6-27B" };
            Assert.Equal(0d, _estimator.Estimate(options, 1000, 1000));
        }

        [Fact]
        public void Estimate_applies_rate_for_known_hosted_model()
        {
            var options = new LlmOptions { BaseUrl = "", Model = "gpt-4o-mini" };
            var cost = _estimator.Estimate(options, 1000, 1000);
            Assert.True(cost > 0d);
        }
    }
}
