using Microsoft.Extensions.DependencyInjection;

namespace SeedForge.Services.Ai
{
    /// <summary>DI wiring for the AI plumbing (options, resolver, cost estimator, client, logging decorator).</summary>
    public static class AiServiceCollectionExtensions
    {
        public static IServiceCollection AddAiServices(this IServiceCollection services, IConfiguration config)
        {
            services.Configure<AiOptions>(config.GetSection("Ai"));
            // Scoped: the resolver now reads ApplicationDbContext (active ConfigProfile) per call.
            services.AddScoped<LlmOptionsResolver>();
            services.AddSingleton<CostEstimator>();
            services.AddSingleton<LlmClient>(); // raw client
            // Decorator chain (outer → inner): FailoverLlmClient → AiCallLogger → LlmClient. The logger writes one
            // AiCallLog per attempt; failover retries a connectivity failure once on the fallback profile's slot.
            services.AddSingleton<AiCallLogger>(sp => new AiCallLogger(
                sp.GetRequiredService<LlmClient>(),
                sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<CostEstimator>(),
                sp.GetRequiredService<ILogger<AiCallLogger>>()));
            services.AddSingleton<ILlmClient>(sp => new FailoverLlmClient(
                sp.GetRequiredService<AiCallLogger>(),
                sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<ILogger<FailoverLlmClient>>()));
            // Scoped: consumes the scoped resolver (avoids a singleton→scoped captive dependency).
            services.AddScoped<ConnectionTester>();
            return services;
        }
    }
}
