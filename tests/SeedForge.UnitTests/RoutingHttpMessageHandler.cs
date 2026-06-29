using System.Net;
using System.Text;

namespace SeedForge.UnitTests
{
    /// <summary>
    /// Test handler that routes by a substring of the request URI to a canned response, so a single client can make the
    /// multiple distinct YouTube calls a real resolution involves (e.g. <c>search</c> then <c>channels</c> then
    /// <c>playlistItems</c>). Never touches the network. Routes are matched in insertion order; the first whose key is a
    /// substring of the absolute URI wins.
    /// </summary>
    public sealed class RoutingHttpMessageHandler : HttpMessageHandler
    {
        private readonly List<(string Match, HttpStatusCode Status, string Body)> _routes = new();

        public Uri? LastRequestUri { get; private set; }
        public int CallCount { get; private set; }

        /// <summary>Registers a route: a request whose URI contains <paramref name="uriContains"/> gets <paramref name="body"/>.</summary>
        public RoutingHttpMessageHandler When(string uriContains, string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            _routes.Add((uriContains, status, body));
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequestUri = request.RequestUri;
            var uri = request.RequestUri?.ToString() ?? string.Empty;

            foreach (var (match, status, body) in _routes)
            {
                if (uri.Contains(match, StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(new HttpResponseMessage(status)
                    {
                        Content = new StringContent(body, Encoding.UTF8, "application/json"),
                    });
                }
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent($"{{\"error\":\"no route for {uri}\"}}", Encoding.UTF8, "application/json"),
            });
        }
    }
}
