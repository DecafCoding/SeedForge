namespace SeedForge.Services.YouTube
{
    /// <summary>Strongly-typed config for the YouTube Data API v3 boundary (bound from the "YouTube" section). ApiKey is blank in source — supply via user-secrets.</summary>
    public class YouTubeOptions
    {
        /// <summary>YouTube Data API key. Passed as a query-string param on every call. Blank in source; set via user-secrets / env.</summary>
        public string ApiKey { get; set; } = string.Empty;

        /// <summary>YouTube Data API v3 base address.</summary>
        public string BaseUrl { get; set; } = "https://www.googleapis.com/youtube/v3";

        /// <summary>How many recent uploads to read from a channel's uploads playlist per poll.</summary>
        public int MaxResults { get; set; } = 20;

        /// <summary>
        /// When true (default), discovery enriches newly found videos with a batched <c>videos.list</c> call
        /// (1 quota unit per ≤50 ids). Set false to spend zero extra quota — discovery then behaves exactly as before.
        /// </summary>
        public bool FetchVideoMetadata { get; set; } = true;
    }
}
