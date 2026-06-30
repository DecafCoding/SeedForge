using System.Net.Http;

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

        /// <summary>
        /// True when this failure means the endpoint could not be reached or served the request — an HTTP 5xx, or a
        /// transport-level error (connection refused / DNS / timeout) wrapped as the inner exception. Distinguishes
        /// "endpoint unavailable" (worth a failover) from a 4xx or a bad/unparseable response (the model answered —
        /// failing over would only double-spend).
        /// </summary>
        public bool IsConnectivityFailure =>
            StatusCode is >= 500
            || InnerException is HttpRequestException
            || InnerException is TaskCanceledException
            || InnerException is TimeoutException;
    }
}
