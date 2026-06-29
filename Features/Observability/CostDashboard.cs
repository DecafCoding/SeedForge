using Microsoft.EntityFrameworkCore;
using SeedForge.Data;

namespace SeedForge.Features.Observability
{
    /// <summary>Token usage and estimated cost for one pipeline stage over a time window.</summary>
    public sealed record StageCost(
        string Stage, int Calls, int PromptTokens, int CompletionTokens, int TotalTokens, double EstimatedCost);

    /// <summary>Token usage and estimated cost for one provider (local rig, or a hosted model) over a window.</summary>
    public sealed record ProviderCost(
        string Provider, bool IsLocal, int Calls, int PromptTokens, int CompletionTokens, int TotalTokens, double EstimatedCost);

    /// <summary>Grand totals across every recorded call in a window.</summary>
    public sealed record CostTotals(
        int Calls, int PromptTokens, int CompletionTokens, int TotalTokens, double EstimatedCost);

    /// <summary>
    /// Read-only aggregation of <see cref="Domain.AiCallLog"/> token usage + estimated cost (grouped by stage and by
    /// provider) plus Apify compute units from transcripts, for the cost/token dashboard. Queries via the context
    /// directly (no repository) and groups in memory — SQLite/EF will not translate every GroupBy+Sum shape, and the
    /// data volume for a single-user tool is small.
    /// </summary>
    public sealed class CostDashboard(ApplicationDbContext db)
    {
        /// <summary>Per-stage call counts, token sums, and summed estimated cost since <paramref name="fromUtc"/>.</summary>
        public async Task<IReadOnlyList<StageCost>> PerStageAsync(DateTime fromUtc, CancellationToken ct = default)
        {
            var rows = await db.AiCallLogs.Where(l => l.CreatedAtUtc >= fromUtc).ToListAsync(ct);
            return rows.GroupBy(l => l.Stage)
                .Select(g => new StageCost(g.Key, g.Count(),
                    g.Sum(x => x.PromptTokens), g.Sum(x => x.CompletionTokens),
                    g.Sum(x => x.TotalTokens), g.Sum(x => x.EstimatedCost)))
                .OrderByDescending(s => s.EstimatedCost).ThenByDescending(s => s.TotalTokens)
                .ToList();
        }

        /// <summary>Per-provider totals; local calls collapse under "local", hosted calls group by model id.</summary>
        public async Task<IReadOnlyList<ProviderCost>> PerProviderAsync(DateTime fromUtc, CancellationToken ct = default)
        {
            var rows = await db.AiCallLogs.Where(l => l.CreatedAtUtc >= fromUtc).ToListAsync(ct);
            return rows.GroupBy(l => l.IsLocal ? "local" : l.Model)
                .Select(g => new ProviderCost(g.Key, g.Any(x => x.IsLocal), g.Count(),
                    g.Sum(x => x.PromptTokens), g.Sum(x => x.CompletionTokens),
                    g.Sum(x => x.TotalTokens), g.Sum(x => x.EstimatedCost)))
                .OrderByDescending(p => p.EstimatedCost).ThenByDescending(p => p.TotalTokens)
                .ToList();
        }

        /// <summary>Grand totals across every call since <paramref name="fromUtc"/> (zeros when there are none).</summary>
        public async Task<CostTotals> TotalsAsync(DateTime fromUtc, CancellationToken ct = default)
        {
            var rows = await db.AiCallLogs.Where(l => l.CreatedAtUtc >= fromUtc).ToListAsync(ct);
            return new CostTotals(rows.Count,
                rows.Sum(x => x.PromptTokens), rows.Sum(x => x.CompletionTokens),
                rows.Sum(x => x.TotalTokens), rows.Sum(x => x.EstimatedCost));
        }

        /// <summary>Sum of Apify compute units across transcripts created since <paramref name="fromUtc"/>.</summary>
        public async Task<double> ApifyCostAsync(DateTime fromUtc, CancellationToken ct = default)
        {
            var units = await db.Transcripts
                .Where(t => t.CreatedAtUtc >= fromUtc)
                .Select(t => t.ApifyCostUnits)
                .ToListAsync(ct);
            return units.Sum(u => u ?? 0);
        }
    }
}
