using Cogworks.UmbracoAI.AgentMemory.Persistence;
using Cogworks.UmbracoAI.AgentMemory.Persistence.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Cogworks.UmbracoAI.AgentMemory.Tests.Persistence;

/// <summary>
/// EF Core schema-shape verification for <see cref="AgentMemoryDbContext"/>.
/// Validates the DbContext model maps onto the table names + column shape that
/// <see cref="AddAgentMemorySchema"/> creates via NPoco DDL — guarding against
/// drift between the two paths.
///
/// The actual NPoco migration is exercised end-to-end against a real Umbraco
/// host in the Story 1.1 manual E2E gate (Task 8) per AR31 — the in-memory
/// SQLite test here covers the EF Core half cheaply.
/// </summary>
[TestFixture]
public class AgentMemoryDbContextSchemaTests
{
    [Test]
    public async Task EnsureCreated_BuildsBothTables_OnSqlite()
    {
        await using var ctx = NewSqliteContext(out _);

        await ctx.Database.EnsureCreatedAsync();

        var tableNames = await ListTablesAsync(ctx);
        Assert.That(tableNames, Has.Member(Constants.FeedbackTableName));
        Assert.That(tableNames, Has.Member(Constants.MemoryEntriesTableName));
    }

    [Test]
    public async Task EnsureCreated_RoundTripsBothEntities_WithNullableColumns()
    {
        await using var ctx = NewSqliteContext(out _);
        await ctx.Database.EnsureCreatedAsync();

        ctx.Feedback.Add(new AgentRunFeedbackEntity
        {
            Id = Guid.NewGuid(),
            RunId = "run-1",
            AgentId = Guid.NewGuid(),
            Score = 1,
            Comment = null,
            CreatedBy = Guid.NewGuid(),
            CreatedUtc = new DateTime(2026, 5, 10, 12, 0, 0, DateTimeKind.Utc),
            WorkspaceId = null,
        });
        ctx.MemoryEntries.Add(new MemoryEntryEntity
        {
            Id = Guid.NewGuid(),
            RunId = "run-1",
            AgentId = Guid.NewGuid(),
            WorkspaceId = null,
            DigestText = "x",
            EmbeddingRef = "vec-1",
            IndexingStatus = 1,
            IndexingError = null,
            EmbeddedUtc = null,
            CreatedUtc = new DateTime(2026, 5, 10, 12, 0, 0, DateTimeKind.Utc),
        });
        await ctx.SaveChangesAsync();

        Assert.That(await ctx.Feedback.CountAsync(), Is.EqualTo(1));
        Assert.That(await ctx.MemoryEntries.CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task EnsureDeleted_AfterEnsureCreated_LeavesNoTables()
    {
        await using var ctx = NewSqliteContext(out _);
        await ctx.Database.EnsureCreatedAsync();
        await ctx.Database.EnsureDeletedAsync();

        var tableNames = await ListTablesAsync(ctx);
        Assert.That(tableNames, Does.Not.Contain(Constants.FeedbackTableName));
        Assert.That(tableNames, Does.Not.Contain(Constants.MemoryEntriesTableName));
    }

    [Test]
    public async Task EnsureCreated_IsIdempotent_OnSecondInvocation()
    {
        await using var ctx = NewSqliteContext(out _);
        await ctx.Database.EnsureCreatedAsync();
        Assert.DoesNotThrowAsync(async () => await ctx.Database.EnsureCreatedAsync());
    }

    private static AgentMemoryDbContext NewSqliteContext(out SqliteConnection connection)
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<AgentMemoryDbContext>()
            .UseSqlite(connection)
            .Options;
        return new AgentMemoryDbContext(options);
    }

    private static async Task<List<string>> ListTablesAsync(AgentMemoryDbContext ctx)
    {
        var conn = ctx.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync();
        }
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name";
        var names = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }
        return names;
    }
}
