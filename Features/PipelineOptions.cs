namespace SeedForge.Features
{
    /// <summary>Pipeline-wide knobs bound from the <c>Pipeline</c> config section.</summary>
    public sealed class PipelineOptions
    {
        /// <summary>An idea survives scoring when the mean of its four axes is at least this value.</summary>
        public double ScoreThreshold { get; set; } = 0.6;
    }
}
