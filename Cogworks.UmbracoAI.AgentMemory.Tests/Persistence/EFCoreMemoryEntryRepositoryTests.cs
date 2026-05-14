using Cogworks.UmbracoAI.AgentMemory.Memory;
using Cogworks.UmbracoAI.AgentMemory.Persistence;
using Cogworks.UmbracoAI.AgentMemory.Persistence.Entities;
using Cogworks.UmbracoAI.AgentMemory.Persistence.Repositories;
using Cogworks.UmbracoAI.AgentMemory.Tests._TestUtilities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Cogworks.UmbracoAI.AgentMemory.Tests.Persistence;

/// <summary>
/// End-to-end persistence verification for <see cref="EFCoreMemoryEntryRepository"/>.
/// Mirror Story 2.1's <c>AgentFeedbackServiceTests</c> + Story 1.1
/// <c>AgentMemoryDbContextSchemaTests</c> Sqlite-in-memory pattern, wired
/// through <see cref="TestEFCoreScopeProvider"/> so the canonical
/// <c>CreateScope → ExecuteWithContextAsync → Complete</c> path is exercised
/// for real.
/// </summary>
[TestFixture]
public class EFCoreMemoryEntryRepositoryTests
{
    private static readonly DateTime FixedUtcNow = new(2026, 5, 14, 12, 0, 0, DateTimeKind.Utc);

    private SqliteConnection _connection = null!;
    private AgentMemoryDbContext _ctx = null!;
    private TestEFCoreScopeProvider _scopeProvider = null!;
    private EFCoreMemoryEntryRepository _repository = null!;

    [SetUp]
    public async Task SetUp()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AgentMemoryDbContext>()
            .UseSqlite(_connection)
            .Options;
        _ctx = new AgentMemoryDbContext(options);
        await _ctx.Database.EnsureCreatedAsync();

        _scopeProvider = new TestEFCoreScopeProvider(_ctx);
        _repository = new EFCoreMemoryEntryRepository(_scopeProvider);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _ctx.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private static MemoryEntryEntity Entry(
        string runId,
        Guid agentId,
        IndexingStatus status = IndexingStatus.Embedded,
        DateTime? createdUtc = null,
        string digestText = "digest",
        string embeddingRef = "doc-1") => new()
    {
        Id = Guid.NewGuid(),
        RunId = runId,
        AgentId = agentId,
        WorkspaceId = null,
        DigestText = digestText,
        EmbeddingRef = embeddingRef,
        IndexingStatus = (int)status,
        IndexingError = null,
        EmbeddedUtc = status == IndexingStatus.Embedded ? createdUtc ?? FixedUtcNow : null,
        CreatedUtc = createdUtc ?? FixedUtcNow,
    };

    [Test]
    public async Task AddAsync_NewEntry_PersistsRowVisibleToFindByRunIdAndAgentId()
    {
        var agentId = Guid.NewGuid();
        var entry = Entry("run-1", agentId);

        await _repository.AddAsync(entry, CancellationToken.None);

        var found = await _repository.FindByRunIdAndAgentIdAsync("run-1", agentId, CancellationToken.None);
        Assert.That(found, Is.Not.Null);
        Assert.That(found!.Id, Is.EqualTo(entry.Id));
        Assert.That(found.DigestText, Is.EqualTo("digest"));
        Assert.That(found.IndexingStatus, Is.EqualTo((int)IndexingStatus.Embedded));
    }

    [Test]
    public async Task UpdateAsync_ExistingEntry_OverwritesMutableFields_PreservesId()
    {
        var agentId = Guid.NewGuid();
        var entry = Entry("run-1", agentId, digestText: "first", embeddingRef: "doc-first");
        await _repository.AddAsync(entry, CancellationToken.None);
        var originalId = entry.Id;

        // Mutate in place — supersede flow.
        entry.DigestText = "second";
        entry.EmbeddingRef = "doc-second";
        entry.IndexingStatus = (int)IndexingStatus.Failed;
        entry.IndexingError = "boom";
        entry.EmbeddedUtc = null;

        await _repository.UpdateAsync(entry, CancellationToken.None);

        // Detach + re-read to bypass identity map.
        _ctx.ChangeTracker.Clear();
        var rows = await _ctx.MemoryEntries.AsNoTracking().ToListAsync();
        Assert.That(rows, Has.Count.EqualTo(1), "UpdateAsync must not INSERT a duplicate row");
        var row = rows[0];
        Assert.Multiple(() =>
        {
            Assert.That(row.Id, Is.EqualTo(originalId), "Id preserved across in-place update");
            Assert.That(row.DigestText, Is.EqualTo("second"));
            Assert.That(row.EmbeddingRef, Is.EqualTo("doc-second"));
            Assert.That(row.IndexingStatus, Is.EqualTo((int)IndexingStatus.Failed));
            Assert.That(row.IndexingError, Is.EqualTo("boom"));
            Assert.That(row.EmbeddedUtc, Is.Null);
        });
    }

    [Test]
    public async Task FindByRunIdAndAgentIdAsync_Existing_ReturnsRow()
    {
        var agentId = Guid.NewGuid();
        await _repository.AddAsync(Entry("run-1", agentId), CancellationToken.None);

        var result = await _repository.FindByRunIdAndAgentIdAsync("run-1", agentId, CancellationToken.None);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.RunId, Is.EqualTo("run-1"));
        Assert.That(result.AgentId, Is.EqualTo(agentId));
    }

    [Test]
    public async Task FindByRunIdAndAgentIdAsync_Missing_ReturnsNull()
    {
        var result = await _repository.FindByRunIdAndAgentIdAsync(
            "run-missing", Guid.NewGuid(), CancellationToken.None);

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task GetByRunIdAsync_Multiple_OrderedByCreatedUtcDescending()
    {
        // The schema indexes on (AgentId, CreatedUtc DESC); GetByRunIdAsync uses
        // RunId as the filter (no composite index on (RunId, AgentId) in v0.1 —
        // Story 5.x optimisation candidate per Task 9c).
        var agentA = Guid.NewGuid();
        var agentB = Guid.NewGuid();
        // Insert oldest first to make the ordering assertion non-trivial.
        await _repository.AddAsync(
            Entry("run-1", agentA, createdUtc: FixedUtcNow.AddMinutes(-5)), CancellationToken.None);
        await _repository.AddAsync(
            Entry("run-1", agentB, createdUtc: FixedUtcNow), CancellationToken.None);

        var rows = await _repository.GetByRunIdAsync("run-1", CancellationToken.None);

        Assert.That(rows, Has.Count.EqualTo(2));
        Assert.That(rows[0].AgentId, Is.EqualTo(agentB),
            "CreatedUtc DESC ordering — agentB (newer) is rows[0]");
        Assert.That(rows[1].AgentId, Is.EqualTo(agentA));
    }

    [TestCase(-1, 0)]
    [TestCase(0, 0)]
    [TestCase(5, 5)]
    [TestCase(150, 100)]
    public async Task GetRecentByAgentIdAsync_Clamps_0_100_NonThrowingly(int requestedTake, int expectedCount)
    {
        var agentId = Guid.NewGuid();
        // Seed 150 rows for the same agent with distinct CreatedUtc to
        // exercise both the index and the clamp.
        for (var i = 0; i < 150; i++)
        {
            await _repository.AddAsync(
                Entry($"run-{i}", agentId, createdUtc: FixedUtcNow.AddSeconds(i)),
                CancellationToken.None);
        }

        var result = await _repository.GetRecentByAgentIdAsync(agentId, requestedTake, CancellationToken.None);

        Assert.That(result, Has.Count.EqualTo(expectedCount));
    }

    [TestCase(IndexingStatus.Pending, 0)]
    [TestCase(IndexingStatus.Embedded, 1)]
    [TestCase(IndexingStatus.Failed, 2)]
    public async Task AddAsync_PersistsIndexingStatusOrdinals_PinsContract(
        IndexingStatus status, int expectedOrdinal)
    {
        var agentId = Guid.NewGuid();
        var entry = Entry($"run-{(int)status}", agentId, status: status);

        await _repository.AddAsync(entry, CancellationToken.None);

        _ctx.ChangeTracker.Clear();
        var row = await _ctx.MemoryEntries.AsNoTracking()
            .Where(e => e.RunId == $"run-{(int)status}")
            .SingleAsync();
        Assert.That(row.IndexingStatus, Is.EqualTo(expectedOrdinal),
            $"{status} must persist as ordinal {expectedOrdinal} (Story 1.1 deferred-work line 11 contract)");
    }
}
