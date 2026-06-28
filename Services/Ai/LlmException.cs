namespace SeedForge.Services.Ai
{
    /// <summary>Thrown when a model call returns a non-2xx status or an unparseable/empty response.</summary>
    public sealed class LlmException : Exception
    {
        public int? StatusCode { get; }
        public string? ResponseBody { get; }

        public LlmException(string message, int? statusCode = null, string? responseBody = null, Exception? inner = null)
            : base(message, inner)
        {
            StatusCode = statusCode;
            ResponseBody = responseBody;
        }
    }
}
