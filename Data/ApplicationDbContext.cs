using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using SeedForge.Domain;

namespace SeedForge.Data
{
    /// <summary>Single EF Core context: ASP.NET Core Identity + the SeedForge domain entity set.</summary>
    public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : IdentityDbContext<ApplicationUser>(options)
    {
        public DbSet<Channel> Channels => Set<Channel>();
        public DbSet<Video> Videos => Set<Video>();
        public DbSet<Transcript> Transcripts => Set<Transcript>();
        public DbSet<Segment> Segments => Set<Segment>();
        public DbSet<Idea> Ideas => Set<Idea>();
        public DbSet<IdeaScore> IdeaScores => Set<IdeaScore>();
        public DbSet<Concept> Concepts => Set<Concept>();
        public DbSet<ConceptJob> ConceptJobs => Set<ConceptJob>();
        public DbSet<AiCallLog> AiCallLogs => Set<AiCallLog>();
        public DbSet<ConfigProfile> ConfigProfiles => Set<ConfigProfile>();

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder); // REQUIRED: configures the Identity tables.

            // Dedup key: at most one row per discovered YouTube video.
            builder.Entity<Video>()
                .HasIndex(v => v.YouTubeVideoId)
                .IsUnique();

            // At most one active Concept per Idea (append-only with one active).
            builder.Entity<Concept>()
                .HasIndex(c => c.IdeaId)
                .IsUnique()
                .HasFilter("\"IsActive\" = 1");

            // At most one active ConfigProfile across the table (mirrors the Concept active-index pattern).
            builder.Entity<ConfigProfile>()
                .HasIndex(p => p.IsActive)
                .IsUnique()
                .HasFilter("\"IsActive\" = 1");

            // Enums stored as legible text rather than ints.
            builder.Entity<Video>().Property(v => v.Status).HasConversion<string>();
            builder.Entity<ConceptJob>().Property(j => j.Status).HasConversion<string>();
            builder.Entity<ConceptJob>().Property(j => j.Trigger).HasConversion<string>();
            builder.Entity<ConceptJob>().Property(j => j.SlotOverride).HasConversion<string>();
            builder.Entity<IdeaScore>().Property(s => s.Slot).HasConversion<string>();
            builder.Entity<Concept>().Property(c => c.Slot).HasConversion<string>();
            builder.Entity<AiCallLog>().Property(l => l.Slot).HasConversion<string>();

            // Relationships: derivative -> source links use Restrict ("stale-not-deleted").
            builder.Entity<Transcript>()
                .HasOne<Video>()
                .WithOne()
                .HasForeignKey<Transcript>(t => t.VideoId)
                .IsRequired(false) // pasted transcripts have no backing Video
                .OnDelete(DeleteBehavior.Restrict);

            builder.Entity<Segment>()
                .HasOne<Transcript>()
                .WithMany()
                .HasForeignKey(s => s.TranscriptId)
                .OnDelete(DeleteBehavior.Restrict);

            builder.Entity<Idea>()
                .HasOne<Segment>()
                .WithMany()
                .HasForeignKey(i => i.SegmentId)
                .OnDelete(DeleteBehavior.Restrict);

            builder.Entity<IdeaScore>()
                .HasOne<Idea>()
                .WithMany()
                .HasForeignKey(s => s.IdeaId)
                .OnDelete(DeleteBehavior.Restrict);

            builder.Entity<Concept>()
                .HasOne<Idea>()
                .WithMany()
                .HasForeignKey(c => c.IdeaId)
                .OnDelete(DeleteBehavior.Restrict);

            builder.Entity<ConceptJob>()
                .HasOne<Idea>()
                .WithMany()
                .HasForeignKey(j => j.IdeaId)
                .OnDelete(DeleteBehavior.Restrict);
        }
    }
}
