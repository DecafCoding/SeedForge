namespace SeedForge.Services.Apify
{
    /// <summary>Thrown when an Apify call returns a non-2xx status, times out, or yields an unparseable response.</summary>
    public sealed class ApifyException : Exception
    {
        public int? StatusCode { get; }
        public string? ResponseBody { get; }

        public ApifyException(string message, int? statusCode = null, string? responseBody = null, Exception? inner = null)
            : base(message, inner)
        {
            StatusCode = statusCode;
            ResponseBody = responseBody;
        }
    }
}
