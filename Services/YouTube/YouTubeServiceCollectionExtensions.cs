using Microsoft.Extensions.Options;

namespace SeedForge.Services.YouTube
{
    /// <summary>DI wiring for the YouTube Data API boundary: options and the typed client (registered against its interface).</summary>
    public static class YouTubeServiceCollectionExtensions
    {
        public static IServiceCollection AddYouTube(this IServiceCollection services, IConfiguration config)
        {
            services.Configure<YouTubeOptions>(config.GetSection("YouTube"));

            services.AddHttpClient<IYouTubeDataClient, YouTubeDataClient>((sp, http) =>
            {
                var o = sp.GetRequiredService<IOptions<YouTubeOptions>>().Value;
                // Trailing slash so relative request paths ("channels?…") resolve under /youtube/v3/.
                var baseUrl = o.BaseUrl.EndsWith('/') ? o.BaseUrl : o.BaseUrl + "/";
                http.BaseAddress = new Uri(baseUrl);
            });

            return services;
        }
    }
}
