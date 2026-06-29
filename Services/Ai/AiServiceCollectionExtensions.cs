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
            services.AddSingleton<ILlmClient>(sp => new AiCallLogger( // decorated: one AiCallLog per call
                sp.GetRequiredService<LlmClient>(),
                sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<CostEstimator>(),
                sp.GetRequiredService<ILogger<AiCallLogger>>()));
            // Scoped: consumes the scoped resolver (avoids a singleton→scoped captive dependency).
            services.AddScoped<ConnectionTester>();
            return services;
        }
    }
}
