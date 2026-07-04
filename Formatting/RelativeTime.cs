namespace SeedForge
{
    /// <summary>
    /// Renders a UTC timestamp as a YouTube-style English "time ago" phrase — e.g. <c>2 days ago</c>,
    /// <c>3 hours ago</c>, <c>just now</c>. Future timestamps collapse to "just now".
    /// </summary>
    public static class RelativeTime
    {
        /// <summary>Formats how long ago <paramref name="utc"/> was, relative to now (e.g. <c>2 days ago</c>).</summary>
        public static string ToRelativeAgo(this DateTime utc)
        {
            var delta = DateTime.UtcNow - utc;
            if (delta < TimeSpan.Zero) delta = TimeSpan.Zero;

            var seconds = delta.TotalSeconds;
            if (seconds < 60) return "just now";

            var minutes = (int)(seconds / 60);
            if (minutes < 60) return $"{minutes} {Plural(minutes, "minute")} ago";

            var hours = (int)(seconds / 3600);
            if (hours < 24) return $"{hours} {Plural(hours, "hour")} ago";

            var days = (int)(seconds / 86400);
            if (days < 7) return $"{days} {Plural(days, "day")} ago";

            if (days < 30)
            {
                var weeks = days / 7;
                return $"{weeks} {Plural(weeks, "week")} ago";
            }

            var months = Math.Max(1, (int)(days / 30.436875));
            if (months < 12) return $"{months} {Plural(months, "month")} ago";

            var years = Math.Max(1, (int)(days / 365.2425));
            return $"{years} {Plural(years, "year")} ago";
        }

        private static string Plural(int n, string unit) => n == 1 ? unit : unit + "s";
    }
}
