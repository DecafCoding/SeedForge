namespace SeedForge.Domain
{
    /// <summary>Immutable source: a YouTube channel the Discovery worker polls for new uploads.</summary>
    public class Channel
    {
        public int Id { get; set; }
        public string YouTubeChannelId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public DateTime AddedAtUtc { get; set; }
        public DateTime? LastPolledUtc { get; set; }
    }
}
