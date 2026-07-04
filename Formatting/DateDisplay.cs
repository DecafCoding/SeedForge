using System.Globalization;

namespace SeedForge
{
    /// <summary>
    /// Single source of truth for how dates/times are rendered in the UI: <c>mm/dd/yyyy h:mmtt</c>
    /// with a lowercase meridiem, e.g. <c>06/30/2026 2:00pm</c>. Values are formatted as-is (the
    /// domain stores UTC), matching the rest of the app.
    /// </summary>
    public static class DateDisplay
    {
        private const string Pattern = "MM/dd/yyyy h:mmtt";

        /// <summary>Formats a date/time as <c>06/30/2026 2:00pm</c>.</summary>
        public static string ToDisplay(this DateTime value) =>
            value.ToString(Pattern, CultureInfo.InvariantCulture).ToLowerInvariant();

        /// <summary>Formats a nullable date/time, returning <paramref name="ifNull"/> (default "—") when null.</summary>
        public static string ToDisplay(this DateTime? value, string ifNull = "—") =>
            value.HasValue ? value.Value.ToDisplay() : ifNull;
    }
}
