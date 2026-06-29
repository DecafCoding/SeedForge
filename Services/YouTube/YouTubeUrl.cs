using System.Text.RegularExpressions;

namespace SeedForge.Services.YouTube
{
    /// <summary>Pure helper that normalizes any common YouTube URL form (or a bare id) to an 11-char video id. Shared with the Phase 6 discovery path.</summary>
    public static partial class YouTubeUrl
    {
        // A YouTube video id is exactly 11 chars of [A-Za-z0-9_-].
        [GeneratedRegex("^[A-Za-z0-9_-]{11}$")]
        private static partial Regex BareIdRegex();

        /// <summary>Extracts a video id from watch?v=, youtu.be/, /shorts/, /embed/ forms (with extra query params) or a bare id. Returns false for junk.</summary>
        public static bool TryGetVideoId(string input, out string id)
        {
            id = string.Empty;
            if (string.IsNullOrWhiteSpace(input)) return false;

            var trimmed = input.Trim();

            // Bare id (no scheme/host) — accept as-is.
            if (BareIdRegex().IsMatch(trimmed))
            {
                id = trimmed;
                return true;
            }

            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            {
                return false;
            }

            var host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

            string? candidate = null;
            if (host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase))
            {
                // https://youtu.be/ID
                candidate = segments.Length > 0 ? segments[0] : null;
            }
            else if (host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase)
                  || host.Equals("m.youtube.com", StringComparison.OrdinalIgnoreCase)
                  || host.Equals("youtube-nocookie.com", StringComparison.OrdinalIgnoreCase))
            {
                if (segments.Length >= 2 &&
                    (segments[0].Equals("shorts", StringComparison.OrdinalIgnoreCase) ||
                     segments[0].Equals("embed", StringComparison.OrdinalIgnoreCase) ||
                     segments[0].Equals("v", StringComparison.OrdinalIgnoreCase)))
                {
                    // https://youtube.com/shorts/ID | /embed/ID | /v/ID
                    candidate = segments[1];
                }
                else
                {
                    // https://www.youtube.com/watch?v=ID&...
                    candidate = ParseQueryValue(uri.Query, "v");
                }
            }

            if (candidate is not null && BareIdRegex().IsMatch(candidate))
            {
                id = candidate;
                return true;
            }

            return false;
        }

        /// <summary>The canonical watch URL for a video id.</summary>
        public static string WatchUrl(string id) => $"https://www.youtube.com/watch?v={id}";

        /// <summary>Reads a single query-string value by key without taking a dependency on System.Web.</summary>
        private static string? ParseQueryValue(string query, string key)
        {
            if (string.IsNullOrEmpty(query)) return null;
            foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                if (eq <= 0) continue;
                if (pair[..eq].Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    return Uri.UnescapeDataString(pair[(eq + 1)..]);
                }
            }
            return null;
        }
    }
}
