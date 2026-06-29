using SeedForge.Features.Concepts;
using SeedForge.Features.Config;
using SeedForge.Features.Extraction;
using SeedForge.Features.Observability;
using SeedForge.Features.Scoring;
using SeedForge.Features.Segmentation;
using SeedForge.Pipeline;

namespace SeedForge.Features
{
    /// <summary>DI wiring for the vertical-slice handlers, pipeline options, and the orchestrator.</summary>
    public static class FeatureServiceCollectionExtensions
    {
        public static IServiceCollection AddFeatures(this IServiceCollection services, IConfiguration config)
        {
            services.Configure<PipelineOptions>(config.GetSection("Pipeline"));

            // The four pipeline slices — each a scoped handler that resolves its slot and persists.
            services.AddScoped<SegmentTranscriptHandler>();
            services.AddScoped<ExtractIdeasHandler>();
            services.AddScoped<ScoreIdeasHandler>();
            services.AddScoped<BuildConceptHandler>();

            // Versioning / compare-loop operations (Phase 3): each co-located with the slice it reuses.
            services.AddScoped<ProfileService>();
            services.AddScoped<RegenerateConceptHandler>();
            services.AddScoped<RescoreIdeaHandler>();
            services.AddScoped<ReplayCallHandler>();

            // Driving-adapter orchestrator that composes the four slices.
            services.AddScoped<PipelineRunner>();

            return services;
        }
    }
}
