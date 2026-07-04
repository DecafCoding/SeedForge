namespace SeedForge.Services.Apify
{
    /// <summary>Strongly-typed config for the Apify boundary (bound from the "Apify" section). Token is blank in source — supply via user-secrets.</summary>
    public class ApifyOptions
    {
        /// <summary>Apify API token. Passed as a query-string param. Blank in source; set via user-secrets / env.</summary>
        public string Token { get; set; } = string.Empty;

        /// <summary>Apify API base address.</summary>
        public string BaseUrl { get; set; } = "https://api.apify.com";

        /// <summary>Actor id; the <c>~</c> separates the user from the actor name and must not be re-encoded.</summary>
        public string ActorId { get; set; } = "streamers~youtube-scraper";

        /// <summary>Request timeout; a synchronous actor run can take minutes.</summary>
        public int TimeoutSeconds { get; set; } = 300;

        /// <summary>
        /// Actor price in USD per 1000 videos returned (pay-per-result). The default matches the
        /// <c>streamers~youtube-scraper</c> actor at $4 / 1000 videos. Per-video cost = value / 1000,
        /// charged once for each video the run actually returns.
        /// </summary>
        public double UsdPerThousandVideos { get; set; } = 4.0;
    }
}
