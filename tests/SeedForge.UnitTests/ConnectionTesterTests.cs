using System.Net;
using SeedForge.Domain;
using SeedForge.Services.Ai;

namespace SeedForge.UnitTests
{
    /// <summary>Proves the tester reports success against a reachable stub and a structured error otherwise (never throws).</summary>
    public class ConnectionTesterTests
    {
        private static LlmOptionsResolver Resolver()
        {
            var options = new AiOptions
            {
                Slots = { ["Seed"] = new LlmOptions { BaseUrl = "http://rig:8070", ApiKey = "local", Model = "m" } },
            };
            return new LlmOptionsResolver(new TestOptionsMonitor<AiOptions>(options));
        }

        [Fact]
        public async Task Returns_ok_when_endpoint_responds()
        {
            var handler = new StubHttpMessageHandler(StubHttpMessageHandler.ChatResponse("pong"));
            var tester = new ConnectionTester(Resolver(), new LlmClient(() => handler));

            var result = await tester.TestConnectionAsync(ModelSlot.Seed);

            Assert.True(result.Ok);
            Assert.Null(result.Error);
        }

        [Fact]
        public async Task Returns_error_when_endpoint_fails()
        {
            var handler = new StubHttpMessageHandler("boom", HttpStatusCode.InternalServerError);
            var tester = new ConnectionTester(Resolver(), new LlmClient(() => handler));

            var result = await tester.TestConnectionAsync(ModelSlot.Seed);

            Assert.False(result.Ok);
            Assert.NotNull(result.Error);
        }
    }
}
