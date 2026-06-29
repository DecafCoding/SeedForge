namespace SeedForge.Services.Queues
{
    /// <summary>Pure drain-time arithmetic for the queue views: pending items × per-item interval.</summary>
    public static class QueueEta
    {
        /// <summary>Drain time for <paramref name="pending"/> items processed one per <paramref name="intervalSeconds"/>.</summary>
        public static TimeSpan Eta(int pending, int intervalSeconds)
        {
            if (pending <= 0 || intervalSeconds <= 0) return TimeSpan.Zero;
            return TimeSpan.FromSeconds((long)pending * intervalSeconds);
        }

        /// <summary>Human-readable drain time; an empty queue reads as a dash, not "0m".</summary>
        public static string Format(int pending, int intervalSeconds)
        {
            var eta = Eta(pending, intervalSeconds);
            if (eta == TimeSpan.Zero) return "—";
            return eta.TotalHours >= 1
                ? $"{(int)eta.TotalHours}h {eta.Minutes}m"
                : $"{eta.Minutes}m {eta.Seconds}s";
        }
    }
}
