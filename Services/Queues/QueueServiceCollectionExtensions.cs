namespace SeedForge.Services.Queues
{
    /// <summary>DI wiring for the durable, DB-backed queues. Both are scoped (they read/write the scoped DbContext).</summary>
    public static class QueueServiceCollectionExtensions
    {
        public static IServiceCollection AddQueues(this IServiceCollection services)
        {
            services.AddScoped<VideoQueue>();
            services.AddScoped<ConceptQueue>();
            return services;
        }
    }
}
