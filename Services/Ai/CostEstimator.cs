namespace SeedForge.Services.Ai
{
    /// <summary>
    /// Estimates the USD cost of a call. Local LAN calls are free (0); hosted calls use a tiny placeholder
    /// per-model rate map (refined once the Concept slot actually targets OpenAI — Phase 3).
    /// </summary>
    public sealed class CostEstimator
    {
        // Placeholder per-1K-token rates (prompt, completion). Unknown models default to 0.
        private static readonly Dictionary<string, (double Prompt, double Completion)> RatesPer1K =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["gpt-4o"] = (0.0025, 0.01),
                ["gpt-4o-mini"] = (0.00015, 0.0006),
            };

        /// <summary>Blank BaseUrl ⇒ hosted OpenAI; an openai.com host ⇒ hosted; any other non-empty host ⇒ local LAN.</summary>
        public bool IsLocal(LlmOptions options)
        {
            if (string.IsNullOrWhiteSpace(options.BaseUrl))
            {
                return false; // hosted OpenAI
            }

            return !options.BaseUrl.Contains("openai.com", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Returns 0 for local calls; otherwise applies the placeholder rate map (0 for unknown models).</summary>
        public double Estimate(LlmOptions options, int promptTokens, int completionTokens)
        {
            if (IsLocal(options))
            {
                return 0d;
            }

            if (!RatesPer1K.TryGetValue(options.Model, out var rate))
            {
                return 0d;
            }

            return (promptTokens / 1000d * rate.Prompt) + (completionTokens / 1000d * rate.Completion);
        }
    }
}
