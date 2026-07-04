namespace SeedForge
{
    /// <summary>
    /// Single source of truth for rendering a video length the way YouTube's site does: hours + minutes
    /// (<c>1h 23m</c>), minutes only when under an hour (<c>23m</c>), and seconds only when under a minute
    /// (<c>45s</c>). Returns "—" for a null/negative value.
    /// </summary>
    public static class DurationDisplay
    {
        /// <summary>Formats a duration in seconds as <c>1h 23m</c> / <c>23m</c> / <c>45s</c>.</summary>
        public static string ToYouTubeLength(this int? seconds)
        {
            if (seconds is not int s || s < 0) return "—";

            var hours = s / 3600;
            var minutes = (s % 3600) / 60;
            var secs = s % 60;

            if (hours > 0) return $"{hours}h {minutes}m";
            if (minutes > 0) return $"{minutes}m";
            return $"{secs}s";
        }
    }
}
