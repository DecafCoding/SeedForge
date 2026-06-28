using Microsoft.Extensions.DependencyInjection;

namespace SeedForge.Services.Ai
{
    /// <summary>DI wiring for the AI plumbing (options, resolver, cost estimator, client, logging decorator).</summary>
    public static class AiServiceCollectionExtensions
    {
        public static IServiceCollection AddAiServices(this IServiceCollection services, IConfiguration config)
        {
            services.Configure<AiOptions>(config.GetSection("Ai"));
            services.AddSingleton<LlmOptionsResolver>();
            services.AddSingleton<CostEstimator>();
            services.AddSingleton<LlmClient>(); // raw client
            services.AddSingleton<ILlmClient>(sp => new AiCallLogger( // decorated: one AiCallLog per call
                sp.GetRequiredService<LlmClient>(),
                sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<CostEstimator>(),
                sp.GetRequiredService<ILogger<AiCallLogger>>()));
            services.AddSingleton<ConnectionTester>();
            return services;
        }
    }
}
