using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SeedForge.Data;
using SeedForge.Domain;
using SeedForge.Services.Ai;

namespace SeedForge.Features.Scoring
{
    public sealed record ScoreIdeasRequest(IReadOnlyList<int> IdeaIds, string CorrelationId);

    /// <summary>One idea's four-axis score, keyed back to the request by its 0-based list index.</summary>
    public sealed record IdeaScoreDto(int Index, double Novelty, double Coherence, double SciFiPotential, double FormulaFit);

    /// <summary>The structured scoring result: one entry per scored idea.</summary>
    public sealed record ScoreIdeasResponse(List<IdeaScoreDto> Scores);

    /// <summary>
    /// Scorer stage (slot Scoring): scores all of a run's ideas in ONE pass on four axes, appends an
    /// <see cref="IdeaScore"/> per matched idea, applies the threshold, and returns the surviving idea ids.
    /// </summary>
    public sealed class ScoreIdeasHandler(
        ApplicationDbContext db, ILlmClient llm, LlmOptionsResolver slots,
        IOptions<PipelineOptions> options, ILogger<ScoreIdeasHandler> log)
    {
        public const string PromptVersion = "v1";

        private const string System =
            "You score science-fiction story premises on four axes, each from 0.0 to 1.0: " +
            "Novelty (freshness of the idea), Coherence (internal logical consistency), " +
            "SciFiPotential (richness as science fiction), and FormulaFit (suitability for a developed story). " +
            "You are given a numbered list of premises. Return one score object per premise, echoing its Index. " +
            "Score every premise you are given.";

        public async Task<IReadOnlyList<int>> HandleAsync(ScoreIdeasRequest req, CancellationToken ct)
        {
            var ideaIds = req.IdeaIds?.ToList() ?? new();
            if (ideaIds.Count == 0)
            {
                return Array.Empty<int>(); // no ideas ⇒ no scoring call, no survivors
            }

            // Load ideas in the request order so the model's Index maps back deterministically.
            var ideasById = await db.Ideas.Where(i => ideaIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);
            var ideas = ideaIds.Where(ideasById.ContainsKey).Select(id => ideasById[id]).ToList();
            if (ideas.Count == 0)
            {
                return Array.Empty<int>();
            }

            var threshold = options.Value.ScoreThreshold;
            var resolved = slots.Resolve(ModelSlot.Scoring);

            var prompt = new StringBuilder("Score each of the following premises:\n");
            for (var i = 0; i < ideas.Count; i++)
            {
                prompt.Append(i).Append(": ").AppendLine(ideas[i].Premise);
            }

            var ctx = new AiCallContext(req.CorrelationId, "Scorer", ModelSlot.Scoring,
                                        nameof(Idea), ideas[0].Id);

            var result = await llm.CompleteStructuredAsync<ScoreIdeasResponse>(
                resolved,
                [new ChatMessage("system", System), new ChatMessage("user", prompt.ToString())],
                ctx, ct);

            var now = DateTime.UtcNow;
            var survivors = new List<int>();
            var scoresToAdd = new List<IdeaScore>();

            foreach (var dto in result.Scores ?? new())
            {
                // Guard against the model returning out-of-range or duplicate indices.
                if (dto.Index < 0 || dto.Index >= ideas.Count) continue;
                var idea = ideas[dto.Index];

                var passed = ((dto.Novelty + dto.Coherence + dto.SciFiPotential + dto.FormulaFit) / 4.0) >= threshold;
                scoresToAdd.Add(new IdeaScore
                {
                    IdeaId = idea.Id,
                    Novelty = dto.Novelty,
                    Coherence = dto.Coherence,
                    SciFiPotential = dto.SciFiPotential,
                    FormulaFit = dto.FormulaFit,
                    PassedThreshold = passed,
                    Slot = ModelSlot.Scoring,
                    Model = resolved.Model,
                    PromptVersion = PromptVersion,
                    CreatedAtUtc = now,
                });
                if (passed) survivors.Add(idea.Id);
            }

            if (scoresToAdd.Count > 0)
            {
                db.IdeaScores.AddRange(scoresToAdd);
                await db.SaveChangesAsync(ct);
            }

            log.LogInformation("Scored {Scored}/{Total} idea(s) in one pass; {Survivors} survived (corr {Corr})",
                scoresToAdd.Count, ideas.Count, survivors.Count, req.CorrelationId);

            return survivors;
        }
    }
}
