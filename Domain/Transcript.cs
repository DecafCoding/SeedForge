namespace SeedForge.Domain
{
    /// <summary>Immutable source: the Apify-extracted transcript for one video.</summary>
    public class Transcript
    {
        public int Id { get; set; }
        public int? VideoId { get; set; }
        public string PlainText { get; set; } = string.Empty;
        public string RawDatasetItemJson { get; set; } = string.Empty;
        public string? Title { get; set; }
        public string? ChannelName { get; set; }
        public double? ApifyCostUnits { get; set; }
        public DateTime CreatedAtUtc { get; set; }
    }
}
