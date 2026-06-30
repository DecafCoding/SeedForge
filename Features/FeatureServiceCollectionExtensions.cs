using SeedForge.Features.Browse;
using SeedForge.Features.Concepts;
using SeedForge.Features.Config;
using SeedForge.Features.Discovery;
using SeedForge.Features.Extraction;
using SeedForge.Features.Ingestion;
using SeedForge.Features.Maintenance;
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

            // Ingestion slice (Phase 4): URL/id → persisted Video + Transcript with status.
            services.AddScoped<IngestTranscriptHandler>();

            // Discovery (Phase 6): channel library + the poll slice.
            services.AddScoped<ChannelLibrary>();
            services.AddScoped<PollChannelsHandler>();

            // Versioning / compare-loop operations (Phase 3): each co-located with the slice it reuses.
            services.AddScoped<ProfileService>();
            services.AddScoped<FailoverSettingsService>();
            services.AddScoped<RegenerateConceptHandler>();
            services.AddScoped<RescoreIdeaHandler>();
            services.AddScoped<ReplayCallHandler>();

            // Cost & token dashboard (Phase 7): read-only aggregation over AiCallLog + Transcript.
            services.AddScoped<CostDashboard>();

            // Browse pages (Phases 9–11): shared read-only projections over the entity graph.
            services.AddScoped<BrowseQueries>();

            // Maintenance (Phase 8): one-shot backfill of video metadata from stored raw Apify items.
            services.AddScoped<BackfillVideoMetadataHandler>();

            // Driving-adapter orchestrator that composes the four slices.
            services.AddScoped<PipelineRunner>();

            return services;
        }
    }
}
