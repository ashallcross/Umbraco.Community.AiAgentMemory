using Microsoft.Extensions.Logging;
using Umbraco.Cms.Infrastructure.Migrations;
using Umbraco.Cms.Infrastructure.Persistence.DatabaseAnnotations;

namespace Umbraco.Community.AiAgentMemory.Persistence.Migrations;

/// <summary>
/// Creates the package's two tables on first install: <see cref="Constants.FeedbackTableName"/>
/// and <see cref="Constants.MemoryEntriesTableName"/>. Runs once via the GUID stamped into
/// <see cref="AgentMemoryMigrationPlan"/>; rerun against an already-migrated host is a no-op
/// (Umbraco's <c>umbracoMigration</c> history table tracks executed steps by GUID, and the
/// per-table <c>TableExists</c> guards inside provide partial-run recovery).
/// </summary>
/// <remarks>
/// Partial-failure recovery contract: <c>MigrationPlanExecutor</c> stamps
/// <c>umbracoKeyValue</c> only for transitions that complete fully — confirmed via
/// <c>MigrationPlansExecutedNotification</c> XML doc ("successful transitions are located
/// in the CompletedTransitions collection"). If <c>CreateMemoryEntriesTable</c> throws after
/// <c>CreateFeedbackTable</c> succeeds, the step's GUID is NOT stamped, the next boot retries
/// the step, and the per-table <c>TableExists</c> guards skip the already-created feedback
/// table and finish the entries table.
/// </remarks>
public sealed class AddAgentMemorySchema : AsyncMigrationBase
{
    public AddAgentMemorySchema(IMigrationContext context)
        : base(context)
    {
    }

    protected override Task MigrateAsync()
    {
        CreateFeedbackTable();
        CreateMemoryEntriesTable();
        return Task.CompletedTask;
    }

    private void CreateFeedbackTable()
    {
        if (TableExists(Constants.FeedbackTableName))
        {
            Logger.LogDebug("Table {Table} already exists — skipping", Constants.FeedbackTableName);
            return;
        }

        Create.Table(Constants.FeedbackTableName)
            .WithColumn("Id").AsGuid().NotNullable().PrimaryKey($"PK_{Constants.FeedbackTableName}")
            .WithColumn("RunId").AsString(256).NotNullable()
            .WithColumn("AgentId").AsGuid().NotNullable()
            .WithColumn("Score").AsInt32().NotNullable()
            .WithColumn("Comment").AsCustom(SpecialDbType.NVARCHARMAX).Nullable()
            .WithColumn("CreatedBy").AsGuid().NotNullable()
            .WithColumn("CreatedUtc").AsDateTime().NotNullable()
            .WithColumn("WorkspaceId").AsGuid().Nullable()
            .Do();

        Create.Index($"IX_{Constants.FeedbackTableName}_AgentId_CreatedUtc")
            .OnTable(Constants.FeedbackTableName)
            .OnColumn("AgentId").Ascending()
            .OnColumn("CreatedUtc").Descending()
            .WithOptions().NonClustered()
            .Do();

        Logger.LogInformation("Created table {Table}", Constants.FeedbackTableName);
    }

    private void CreateMemoryEntriesTable()
    {
        if (TableExists(Constants.MemoryEntriesTableName))
        {
            Logger.LogDebug("Table {Table} already exists — skipping", Constants.MemoryEntriesTableName);
            return;
        }

        Create.Table(Constants.MemoryEntriesTableName)
            .WithColumn("Id").AsGuid().NotNullable().PrimaryKey($"PK_{Constants.MemoryEntriesTableName}")
            .WithColumn("RunId").AsString(256).NotNullable()
            .WithColumn("AgentId").AsGuid().NotNullable()
            .WithColumn("WorkspaceId").AsGuid().Nullable()
            .WithColumn("DigestText").AsCustom(SpecialDbType.NVARCHARMAX).NotNullable()
            .WithColumn("EmbeddingRef").AsString(256).NotNullable()
            .WithColumn("IndexingStatus").AsInt32().NotNullable()
            .WithColumn("IndexingError").AsCustom(SpecialDbType.NVARCHARMAX).Nullable()
            .WithColumn("EmbeddedUtc").AsDateTime().Nullable()
            .WithColumn("CreatedUtc").AsDateTime().NotNullable()
            .Do();

        Create.Index($"IX_{Constants.MemoryEntriesTableName}_AgentId_CreatedUtc")
            .OnTable(Constants.MemoryEntriesTableName)
            .OnColumn("AgentId").Ascending()
            .OnColumn("CreatedUtc").Descending()
            .WithOptions().NonClustered()
            .Do();

        Logger.LogInformation("Created table {Table}", Constants.MemoryEntriesTableName);
    }
}
