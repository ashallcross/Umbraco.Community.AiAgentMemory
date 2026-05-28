using Umbraco.Community.AiAgentMemory.Feedback;
using Umbraco.Community.AiAgentMemory.Persistence;
using Umbraco.Community.AiAgentMemory.Persistence.Entities;
using Umbraco.Community.AiAgentMemory.Persistence.Repositories;
using Umbraco.Community.AiAgentMemory.Tests._TestUtilities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Umbraco.Community.AiAgentMemory.Tests.Feedback;

/// <summary>
/// End-to-end persistence verification for <see cref="AgentFeedbackService"/>.
/// Drives the service against a real <see cref="AgentMemoryDbContext"/> backed
/// by Sqlite in-memory (mirror Story 1.1 <c>AgentMemoryDbContextSchemaTests</c>),
/// wired through <see cref="TestEFCoreScopeProvider"/> so the canonical
/// <c>CreateScope → ExecuteWithContextAsync → Complete</c> pattern is exercised
/// for real.
/// </summary>
[TestFixture]
public class AgentFeedbackServiceTests
{
    private static readonly DateTime FixedUtcNow = new(2026, 5, 12, 10, 0, 0, DateTimeKind.Utc);

    private SqliteConnection _connection = null!;
    private AgentMemoryDbContext _ctx = null!;
    private TestEFCoreScopeProvider _scopeProvider = null!;
    private EFCoreAgentRunFeedbackRepository _repository = null!;
    private FakeTimeProvider _timeProvider = null!;
    private AgentFeedbackService _service = null!;

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
        _repository = new EFCoreAgentRunFeedbackRepository(_scopeProvider);
        _timeProvider = new FakeTimeProvider(new DateTimeOffset(FixedUtcNow, TimeSpan.Zero));
        _service = new AgentFeedbackService(
            _repository,
            NullLogger<AgentFeedbackService>.Instance,
            _timeProvider);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _ctx.DisposeAsync();
        await _connection.DisposeAsync();
    }

    // ─────────────────────────────────────────────────────────────────────
    // AC3 — supersede contract (fresh write + supersede write)
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public async Task RecordFeedback_FreshRunAndUser_PersistsRow()
    {
        var agentId = Guid.NewGuid();
        var createdBy = Guid.NewGuid();

        await _service.RecordFeedbackAsync(
            "run-1", agentId, FeedbackScore.ThumbsUp, comment: null, createdBy,
            CancellationToken.None);

        var rows = await _ctx.Feedback.AsNoTracking().ToListAsync();
        Assert.That(rows, Has.Count.EqualTo(1));
        var row = rows[0];
        Assert.Multiple(() =>
        {
            Assert.That(row.RunId, Is.EqualTo("run-1"));
            Assert.That(row.AgentId, Is.EqualTo(agentId));
            Assert.That(row.Score, Is.EqualTo(0));                       // AC2: ThumbsUp == 0
            Assert.That(row.Comment, Is.Null);
            Assert.That(row.CreatedBy, Is.EqualTo(createdBy));
            Assert.That(row.CreatedUtc, Is.EqualTo(FixedUtcNow));        // FakeTimeProvider value
            Assert.That(row.WorkspaceId, Is.Null);                       // FR33 / FR36 v0.1
        });
    }

    [Test]
    public async Task RecordFeedback_SecondSubmissionSameUser_SupersedesNotDuplicates()
    {
        var agentId = Guid.NewGuid();
        var createdBy = Guid.NewGuid();

        await _service.RecordFeedbackAsync(
            "run-1", agentId, FeedbackScore.ThumbsUp, comment: null, createdBy,
            CancellationToken.None);

        var firstId = (await _ctx.Feedback.AsNoTracking().SingleAsync()).Id;

        _timeProvider.Advance(TimeSpan.FromMinutes(1));

        await _service.RecordFeedbackAsync(
            "run-1", agentId, FeedbackScore.ThumbsDown, comment: "actually wrong", createdBy,
            CancellationToken.None);

        var rows = await _ctx.Feedback.AsNoTracking().ToListAsync();
        Assert.That(rows, Has.Count.EqualTo(1), "supersede must NOT INSERT a duplicate");
        var row = rows[0];
        Assert.Multiple(() =>
        {
            Assert.That(row.Id, Is.EqualTo(firstId), "supersede preserves Id (UPDATE in place)");
            Assert.That(row.Score, Is.EqualTo(1));                       // ThumbsDown
            Assert.That(row.Comment, Is.EqualTo("actually wrong"));
            Assert.That(row.RunId, Is.EqualTo("run-1"));
            Assert.That(row.AgentId, Is.EqualTo(agentId));
            Assert.That(row.CreatedBy, Is.EqualTo(createdBy));
        });
    }

    [Test]
    public async Task RecordFeedback_SecondSubmissionSameUser_UpdatesCreatedUtcToSupersedeTime()
    {
        var agentId = Guid.NewGuid();
        var createdBy = Guid.NewGuid();

        await _service.RecordFeedbackAsync(
            "run-1", agentId, FeedbackScore.ThumbsUp, comment: null, createdBy,
            CancellationToken.None);
        var firstWriteTime = (await _ctx.Feedback.AsNoTracking().SingleAsync()).CreatedUtc;

        _timeProvider.Advance(TimeSpan.FromMinutes(5));

        await _service.RecordFeedbackAsync(
            "run-1", agentId, FeedbackScore.ThumbsDown, comment: null, createdBy,
            CancellationToken.None);

        var row = await _ctx.Feedback.AsNoTracking().SingleAsync();
        Assert.That(row.CreatedUtc, Is.GreaterThan(firstWriteTime),
            "AC3.a — CreatedUtc updated to supersede time, NOT preserved at original");
        Assert.That(row.CreatedUtc, Is.EqualTo(FixedUtcNow.AddMinutes(5)));
    }

    // ─────────────────────────────────────────────────────────────────────
    // AC4 — multi-user distinctness
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public async Task RecordFeedback_TwoUsersOnSameRun_PersistsTwoDistinctRows()
    {
        var agentId = Guid.NewGuid();
        var user1 = Guid.NewGuid();
        var user2 = Guid.NewGuid();

        await _service.RecordFeedbackAsync(
            "run-1", agentId, FeedbackScore.ThumbsUp, comment: null, user1,
            CancellationToken.None);
        await _service.RecordFeedbackAsync(
            "run-1", agentId, FeedbackScore.ThumbsDown, comment: null, user2,
            CancellationToken.None);

        var rows = await _ctx.Feedback.AsNoTracking().ToListAsync();
        Assert.That(rows, Has.Count.EqualTo(2),
            "FR15 — supersede is keyed on (RunId, CreatedBy); distinct users do NOT collapse");
        Assert.That(rows.Select(r => r.CreatedBy), Is.EquivalentTo(new[] { user1, user2 }));
    }

    // ─────────────────────────────────────────────────────────────────────
    // AC5 — CreatedBy provenance (NFR-S7)
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public async Task RecordFeedback_PropagatesCreatedByVerbatim()
    {
        var createdBy = Guid.NewGuid();

        await _service.RecordFeedbackAsync(
            "run-1", Guid.NewGuid(), FeedbackScore.Neutral, comment: null, createdBy,
            CancellationToken.None);

        var row = await _ctx.Feedback.AsNoTracking().SingleAsync();
        Assert.That(row.CreatedBy, Is.EqualTo(createdBy),
            "NFR-S7 — caller-supplied createdBy persists byte-identical");
    }

    // ─────────────────────────────────────────────────────────────────────
    // AC2 — enum-ordinal persistence pin
    // ─────────────────────────────────────────────────────────────────────

    [TestCase(FeedbackScore.ThumbsUp, 0)]
    [TestCase(FeedbackScore.ThumbsDown, 1)]
    [TestCase(FeedbackScore.Neutral, 2)]
    public async Task RecordFeedback_PersistsScoreEnumOrdinals_PinsContract(
        FeedbackScore score, int expectedOrdinal)
    {
        await _service.RecordFeedbackAsync(
            $"run-{(int)score}", Guid.NewGuid(), score, comment: null, Guid.NewGuid(),
            CancellationToken.None);

        var row = await _ctx.Feedback.AsNoTracking()
            .Where(f => f.RunId == $"run-{(int)score}")
            .SingleAsync();
        Assert.That(row.Score, Is.EqualTo(expectedOrdinal),
            $"AC2 — {score} must persist as ordinal {expectedOrdinal} (deferred-work.md Story 1.1 pin)");
    }

    [Test]
    public async Task GetFeedbackForRun_UnknownScoreOrdinal_MapsToNeutralDefensively()
    {
        // Bypass the service to plant a row whose Score column is outside the
        // ThumbsUp=0/ThumbsDown=1/Neutral=2 contract — simulates manual SQL
        // tampering or a future enum addition not yet known to this build.
        var row = new AgentRunFeedbackEntity
        {
            Id = Guid.NewGuid(),
            RunId = "run-unknown",
            AgentId = Guid.NewGuid(),
            Score = 99,
            Comment = null,
            CreatedBy = Guid.NewGuid(),
            CreatedUtc = FixedUtcNow,
            WorkspaceId = null,
        };
        _ctx.Feedback.Add(row);
        await _ctx.SaveChangesAsync();

        var result = await _service.GetFeedbackForRunAsync("run-unknown", CancellationToken.None);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Score, Is.EqualTo(FeedbackScore.Neutral),
            "unknown ordinals must map to Neutral (mirror AgentRunReader.MapStatus defensive pattern)");
    }

    // ─────────────────────────────────────────────────────────────────────
    // AC4 + AC6 — retrieval ordering
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public async Task GetFeedbackForRun_ReturnsAllRowsForRun_OrderedByCreatedUtcDesc()
    {
        // Spec Task 7i: 3 writes (2 users, U1 superseding mid-test at distinct
        // timestamps) → 2 rows returned (U1's supersede collapses to one row);
        // ordering is CreatedUtc DESC.
        var agentId = Guid.NewGuid();
        var user1 = Guid.NewGuid();
        var user2 = Guid.NewGuid();

        await _service.RecordFeedbackAsync(
            "run-1", agentId, FeedbackScore.ThumbsUp, null, user1, CancellationToken.None);
        _timeProvider.Advance(TimeSpan.FromMinutes(1));
        await _service.RecordFeedbackAsync(
            "run-1", agentId, FeedbackScore.ThumbsDown, null, user2, CancellationToken.None);
        _timeProvider.Advance(TimeSpan.FromMinutes(1));
        await _service.RecordFeedbackAsync(
            "run-1", agentId, FeedbackScore.Neutral, "reconsidered", user1, CancellationToken.None);

        var result = await _service.GetFeedbackForRunAsync("run-1", CancellationToken.None);

        Assert.That(result, Has.Count.EqualTo(2),
            "U1's supersede must collapse to a single row at retrieval (3 writes → 2 rows)");
        Assert.That(result[0].CreatedBy, Is.EqualTo(user1),
            "ordered CreatedUtc DESC — U1's supersede is the most recent CreatedUtc");
        Assert.That(result[0].Score, Is.EqualTo(FeedbackScore.Neutral),
            "U1's row carries the superseded score/comment");
        Assert.That(result[0].Comment, Is.EqualTo("reconsidered"));
        Assert.That(result[1].CreatedBy, Is.EqualTo(user2));
    }

    // ─────────────────────────────────────────────────────────────────────
    // AC6 — take clamping (mirror IAgentRunReader contract)
    // ─────────────────────────────────────────────────────────────────────

    [TestCase(-1, 0)]
    [TestCase(0, 0)]
    [TestCase(5, 5)]
    [TestCase(150, 100)]
    public async Task GetRecentForAgent_TakeClampedToRange(int requestedTake, int expectedCount)
    {
        var agentId = Guid.NewGuid();
        // Seed 150 rows for the same agent across distinct users / runs.
        for (var i = 0; i < 150; i++)
        {
            await _service.RecordFeedbackAsync(
                $"run-{i}", agentId, FeedbackScore.ThumbsUp, null, Guid.NewGuid(),
                CancellationToken.None);
            _timeProvider.Advance(TimeSpan.FromSeconds(1));
        }

        var result = await _service.GetRecentForAgentAsync(agentId, requestedTake, CancellationToken.None);

        Assert.That(result, Has.Count.EqualTo(expectedCount));
    }

    // ─────────────────────────────────────────────────────────────────────
    // OperationCanceledException propagation (Story 1.2 carry-forward pattern)
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public void OperationCanceledException_PropagatesUnwrapped_OnRecordFeedback()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.That(
            async () => await _service.RecordFeedbackAsync(
                "run-1", Guid.NewGuid(), FeedbackScore.ThumbsUp, null, Guid.NewGuid(),
                cts.Token),
            Throws.InstanceOf<OperationCanceledException>(),
            "cancellation must NEVER be swallowed; the write path is loud (Story 1.2 carry-forward)");
    }
}
