using Umbraco.Community.AiAgentMemory.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Umbraco.Community.AiAgentMemory.Persistence;

/// <summary>
/// EF Core context owning ONLY the package's two tables. Composes alongside —
/// never replaces or extends — Umbraco's <c>UmbracoDbContext</c> or
/// Umbraco.AI.Persistence's <c>UmbracoAIDbContext</c> (AR2). Schema creation is
/// owned by <see cref="Migrations.AgentMemoryMigrationPlan"/>; this context is
/// the read/write surface only.
/// </summary>
public sealed class AgentMemoryDbContext : DbContext
{
    public AgentMemoryDbContext(DbContextOptions<AgentMemoryDbContext> options)
        : base(options)
    {
    }

    public DbSet<AgentRunFeedbackEntity> Feedback => Set<AgentRunFeedbackEntity>();

    public DbSet<MemoryEntryEntity> MemoryEntries => Set<MemoryEntryEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<AgentRunFeedbackEntity>(b =>
        {
            b.ToTable(Constants.FeedbackTableName);
            b.HasKey(e => e.Id);
            b.Property(e => e.RunId).IsRequired().HasMaxLength(256);
            b.Property(e => e.Comment).IsRequired(false);
            b.HasIndex(e => new { e.AgentId, e.CreatedUtc })
                .IsDescending(false, true)
                .HasDatabaseName("IX_cogworks_agent_memory_feedback_AgentId_CreatedUtc");
        });

        modelBuilder.Entity<MemoryEntryEntity>(b =>
        {
            b.ToTable(Constants.MemoryEntriesTableName);
            b.HasKey(e => e.Id);
            b.Property(e => e.RunId).IsRequired().HasMaxLength(256);
            b.Property(e => e.EmbeddingRef).IsRequired().HasMaxLength(256);
            b.Property(e => e.DigestText).IsRequired();
            b.Property(e => e.IndexingError).IsRequired(false);
            b.Property(e => e.EmbeddedUtc).IsRequired(false);
            b.HasIndex(e => new { e.AgentId, e.CreatedUtc })
                .IsDescending(false, true)
                .HasDatabaseName("IX_cogworks_agent_memory_entries_AgentId_CreatedUtc");
        });
    }
}
