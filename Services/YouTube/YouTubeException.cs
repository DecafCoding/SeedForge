namespace SeedForge.Services.YouTube
{
    /// <summary>Thrown when a YouTube Data API call returns a non-2xx status, yields an unparseable response, or a reference can't be resolved.</summary>
    public sealed class YouTubeException : Exception
    {
        public int? StatusCode { get; }
        public string? ResponseBody { get; }

        public YouTubeException(string message, int? statusCode = null, string? responseBody = null, Exception? inner = null)
            : base(message, inner)
        {
            StatusCode = statusCode;
            ResponseBody = responseBody;
        }
    }
}
