using Microsoft.Extensions.Options;

namespace SeedForge.Services.Apify
{
    /// <summary>DI wiring for the Apify boundary: options, the typed client, and the ingestion service.</summary>
    public static class ApifyServiceCollectionExtensions
    {
        public static IServiceCollection AddApifyServices(this IServiceCollection services, IConfiguration config)
        {
            services.Configure<ApifyOptions>(config.GetSection("Apify"));

            services.AddHttpClient<ApifyClient>((sp, http) =>
            {
                var o = sp.GetRequiredService<IOptions<ApifyOptions>>().Value;
                http.BaseAddress = new Uri(o.BaseUrl);
                http.Timeout = TimeSpan.FromSeconds(o.TimeoutSeconds);
            });

            services.AddScoped<IApifyIngestionService, ApifyIngestionService>();
            return services;
        }
    }
}
