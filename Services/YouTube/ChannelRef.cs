using System.Text.RegularExpressions;

namespace SeedForge.Services.YouTube
{
    /// <summary>How a parsed channel reference should be resolved against the YouTube Data API.</summary>
    public enum ChannelRefKind
    {
        /// <summary>A canonical channel id (<c>UC…</c>); resolve via <c>channels.list?id=</c>.</summary>
        Id,
        /// <summary>An <c>@handle</c>; resolve via <c>channels.list?forHandle=</c>.</summary>
        Handle,
        /// <summary>A legacy custom name (<c>/c/…</c>, <c>/user/…</c>); resolve via the <c>search.list</c> fallback.</summary>
        Custom,
    }

    /// <summary>
    /// Pure helper that classifies a user-supplied channel reference — a bare channel id, a <c>/channel/UC…</c> URL,
    /// an <c>@handle</c> (bare or as a <c>youtube.com/@handle</c> URL), or a legacy custom (<c>/c/…</c>, <c>/user/…</c>)
    /// reference — into a <see cref="ChannelRefKind"/> + normalized value the <see cref="YouTubeDataClient"/> can resolve.
    /// Mirrors the static-helper style of <see cref="YouTubeUrl"/>.
    /// </summary>
    public sealed partial record ChannelRef(ChannelRefKind Kind, string Value)
    {
        // A YouTube channel id is "UC" followed by 22 chars of [A-Za-z0-9_-] (24 total).
        [GeneratedRegex("^UC[A-Za-z0-9_-]{22}$")]
        private static partial Regex ChannelIdRegex();

        // A handle is "@" + 3..30 chars of [A-Za-z0-9_.-] (per YouTube handle rules; lenient on the upper bound).
        [GeneratedRegex("^@[A-Za-z0-9_.-]+$")]
        private static partial Regex HandleRegex();

        public static ChannelRef Id(string id) => new(ChannelRefKind.Id, id);
        public static ChannelRef Handle(string handle) => new(ChannelRefKind.Handle, handle);
        public static ChannelRef Custom(string name) => new(ChannelRefKind.Custom, name);

        /// <summary>
        /// Classifies <paramref name="input"/>. Throws <see cref="ArgumentException"/> for empty / unrecognizable input
        /// so the caller surfaces a clear add-time error rather than making a doomed API call.
        /// </summary>
        public static ChannelRef Parse(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                throw new ArgumentException("Channel reference is empty.", nameof(input));
            }

            var trimmed = input.Trim();

            // Bare channel id (UC…) or bare @handle, with no scheme/host.
            if (ChannelIdRegex().IsMatch(trimmed)) return Id(trimmed);
            if (HandleRegex().IsMatch(trimmed)) return Handle(trimmed);

            if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                var host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
                if (host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase)
                 || host.Equals("m.youtube.com", StringComparison.OrdinalIgnoreCase))
                {
                    var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    if (segments.Length >= 1)
                    {
                        var first = segments[0];

                        // /@handle
                        if (first.StartsWith('@') && HandleRegex().IsMatch(first)) return Handle(first);

                        // /channel/UC…
                        if (first.Equals("channel", StringComparison.OrdinalIgnoreCase) && segments.Length >= 2
                            && ChannelIdRegex().IsMatch(segments[1]))
                        {
                            return Id(segments[1]);
                        }

                        // /c/Name or /user/Name — legacy custom, needs the search fallback.
                        if ((first.Equals("c", StringComparison.OrdinalIgnoreCase)
                          || first.Equals("user", StringComparison.OrdinalIgnoreCase)) && segments.Length >= 2)
                        {
                            return Custom(segments[1]);
                        }
                    }
                }
            }

            // Anything else (a legacy custom name pasted bare, e.g. "LinusTechTips") → best-effort search fallback.
            return Custom(trimmed);
        }
    }
}
