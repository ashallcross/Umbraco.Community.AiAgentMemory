using Cogworks.UmbracoAI.AgentMemory.Feedback;
using Cogworks.UmbracoAI.AgentMemory.Persistence.Entities;
using Cogworks.UmbracoAI.AgentMemory.Persistence.Repositories;
using Cogworks.UmbracoAI.AgentMemory.Web.Api;
using Cogworks.UmbracoAI.AgentMemory.Web.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute.ExceptionExtensions;
using Umbraco.AI.Agent.Core.Agents;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Services;

namespace Cogworks.UmbracoAI.AgentMemory.Tests.Web.Api;

/// <summary>
/// Story 4.9 AC7.b — tests for <see cref="MemoryEntriesReadController"/>.
/// AR30 density: 1 happy/empty + 1 happy/full + 1 override (no feedback) +
/// 3 edges (user-throw / agent-throw / OCE-rethrow). 401 not unit-tested
/// (framework-handled by <c>[Authorize]</c>; covered by manual gate).
/// </summary>
[TestFixture]
public class MemoryEntriesReadControllerTests
{
    private IMemoryEntryRepository _repository = null!;
    private IAgentFeedbackService _feedbackService = null!;
    private IUserService _userService = null!;
    private IAIAgentService _agentService = null!;
    private ILogger<MemoryEntriesReadController> _logger = null!;
    private MemoryEntriesReadController _controller = null!;

    private static readonly DateTime FixedUtcNow = new(2026, 5, 21, 12, 0, 0, DateTimeKind.Utc);

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<IMemoryEntryRepository>();
        _feedbackService = Substitute.For<IAgentFeedbackService>();
        _userService = Substitute.For<IUserService>();
        _agentService = Substitute.For<IAIAgentService>();
        _logger = Substitute.For<ILogger<MemoryEntriesReadController>>();

        // Default stubs — empty everywhere; per-test overrides set the
        // happy-path data.
        _repository.GetRecentAcrossAgentsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<MemoryEntryEntity>>(Array.Empty<MemoryEntryEntity>()));
        _agentService.GetAgentsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IEnumerable<AIAgent>>(Array.Empty<AIAgent>()));
        _userService.GetAsync(Arg.Any<IEnumerable<Guid>>())
            .Returns(Task.FromResult<IEnumerable<IUser>>(Array.Empty<IUser>()));
        _feedbackService.GetFeedbackForRunAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentRunFeedback>>(Array.Empty<AgentRunFeedback>()));

        _controller = new MemoryEntriesReadController(
            _repository, _feedbackService, _userService, _agentService, _logger);
    }

    [TearDown]
    public void TearDown()
    {
        _controller?.Dispose();
    }

    private static MemoryEntryEntity Entry(
        string runId,
        Guid agentId,
        DateTime? createdUtc = null,
        string digestText = "digest") => new()
    {
        Id = Guid.NewGuid(),
        RunId = runId,
        AgentId = agentId,
        WorkspaceId = null,
        DigestText = digestText,
        EmbeddingRef = "doc-1",
        IndexingStatus = 1,
        IndexingError = null,
        EmbeddedUtc = createdUtc ?? FixedUtcNow,
        CreatedUtc = createdUtc ?? FixedUtcNow,
    };

    private static AgentRunFeedback Feedback(
        string runId,
        Guid agentId,
        FeedbackScore score = FeedbackScore.ThumbsDown,
        string? comment = "canonical comment",
        Guid? createdBy = null,
        DateTime? createdUtc = null) => new(
        Id: Guid.NewGuid(),
        RunId: runId,
        AgentId: agentId,
        Score: score,
        Comment: comment,
        CreatedBy: createdBy ?? Guid.NewGuid(),
        CreatedUtc: createdUtc ?? FixedUtcNow);

    private void SeedUsers(params (Guid Key, string Name)[] users)
    {
        var fakeUsers = users.Select(u =>
        {
            var user = Substitute.For<IUser>();
            user.Key.Returns(u.Key);
            user.Name.Returns(u.Name);
            return user;
        }).ToArray();
        _userService.GetAsync(Arg.Any<IEnumerable<Guid>>())
            .Returns(Task.FromResult<IEnumerable<IUser>>(fakeUsers));
    }

    private void SeedAgents(params (Guid Id, string Alias, string Name)[] agents)
    {
        // AIAgent.Id has `internal set` per Story 4.8 Task 0a finding; the
        // package's InternalsVisibleTo to the tests assembly does NOT cover
        // the Umbraco.AI.Agent.Core assembly. Use reflection to set Id, OR
        // accept that the test fixture must take a real Id from upstream's
        // construction path. Workaround: NSubstitute IAIAgent — but the
        // controller's projection reads `agent.Id` + `agent.Name`, so a real
        // AIAgent with reflection-set Id is the cleanest fixture shape.
        var fakeAgents = agents.Select(a =>
        {
            var agent = new AIAgent { Alias = a.Alias, Name = a.Name };
            typeof(AIAgent).GetProperty(nameof(AIAgent.Id))!
                .SetValue(agent, a.Id);
            return agent;
        }).ToArray();
        _agentService.GetAgentsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IEnumerable<AIAgent>>(fakeAgents));
    }

    // ─────────────────────────────────────────────────────────────────────
    // AC7.b.i — happy/empty path (no entries)
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public async Task GetAsync_NoEntries_Returns200WithEmptyArray()
    {
        var result = await _controller.GetAsync(take: null, CancellationToken.None);

        var ok = result as OkObjectResult;
        Assert.That(ok, Is.Not.Null);
        var response = ok!.Value as MemoryWallListResponse;
        Assert.That(response, Is.Not.Null);
        Assert.That(response!.Entries, Is.Empty,
            "Empty repo returns 200 OK + Entries: [] per AC2.e empty-array contract");
        // Early-return path skips the agent + user lookups entirely.
        await _agentService.DidNotReceiveWithAnyArgs().GetAgentsAsync(default);
        await _userService.DidNotReceiveWithAnyArgs().GetAsync(default(IEnumerable<Guid>)!);
    }

    // ─────────────────────────────────────────────────────────────────────
    // AC7.b.ii — happy/full path (3 entries × 2 agents)
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public async Task GetAsync_EntriesWithFeedback_ProjectsScoreCommentAndDisplayNames()
    {
        var agentA = Guid.NewGuid();
        var agentB = Guid.NewGuid();
        var userX = Guid.NewGuid();
        var userY = Guid.NewGuid();

        var entries = new[]
        {
            Entry("run-1", agentA, createdUtc: FixedUtcNow.AddSeconds(3)),
            Entry("run-2", agentB, createdUtc: FixedUtcNow.AddSeconds(2)),
            Entry("run-3", agentA, createdUtc: FixedUtcNow.AddSeconds(1)),
        };
        _repository.GetRecentAcrossAgentsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<MemoryEntryEntity>>(entries));

        _feedbackService.GetFeedbackForRunAsync("run-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentRunFeedback>>(
                new[] { Feedback("run-1", agentA, FeedbackScore.ThumbsDown, "first comment", userX) }));
        _feedbackService.GetFeedbackForRunAsync("run-2", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentRunFeedback>>(
                new[] { Feedback("run-2", agentB, FeedbackScore.ThumbsUp, "second comment", userY) }));
        _feedbackService.GetFeedbackForRunAsync("run-3", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentRunFeedback>>(
                new[] { Feedback("run-3", agentA, FeedbackScore.ThumbsDown, "third comment", userX) }));

        SeedAgents(
            (agentA, "brand-voice-auditor", "Brand Voice Auditor"),
            (agentB, "tone-checker", "Tone Checker"));
        SeedUsers(
            (userX, "Adam Editor"),
            (userY, "Mara Editor"));

        var result = await _controller.GetAsync(take: 100, CancellationToken.None);

        var ok = result as OkObjectResult;
        Assert.That(ok, Is.Not.Null);
        var response = ok!.Value as MemoryWallListResponse;
        Assert.That(response, Is.Not.Null);
        Assert.That(response!.Entries, Has.Count.EqualTo(3));

        Assert.Multiple(() =>
        {
            // run-1 (agentA, userX)
            var row1 = response.Entries[0];
            Assert.That(row1.RunId, Is.EqualTo("run-1"));
            Assert.That(row1.AgentId, Is.EqualTo(agentA));
            Assert.That(row1.AgentDisplayName, Is.EqualTo("Brand Voice Auditor"));
            Assert.That(row1.Score, Is.EqualTo(FeedbackScore.ThumbsDown));
            Assert.That(row1.FeedbackComment, Is.EqualTo("first comment"));
            Assert.That(row1.CreatedBy, Is.EqualTo(userX));
            Assert.That(row1.CreatedByDisplayName, Is.EqualTo("Adam Editor"));

            // run-2 (agentB, userY)
            var row2 = response.Entries[1];
            Assert.That(row2.AgentDisplayName, Is.EqualTo("Tone Checker"));
            Assert.That(row2.Score, Is.EqualTo(FeedbackScore.ThumbsUp));
            Assert.That(row2.CreatedByDisplayName, Is.EqualTo("Mara Editor"));

            // run-3 (agentA, userX)
            var row3 = response.Entries[2];
            Assert.That(row3.AgentDisplayName, Is.EqualTo("Brand Voice Auditor"));
            Assert.That(row3.CreatedByDisplayName, Is.EqualTo("Adam Editor"));
        });
    }

    // ─────────────────────────────────────────────────────────────────────
    // AC7.b.iii — override path: entry with NO feedback row
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public async Task GetAsync_EntryWithNoFeedback_ProjectsNullScoreCommentCreatedBy()
    {
        var agentA = Guid.NewGuid();
        var userX = Guid.NewGuid();

        var entries = new[]
        {
            Entry("run-with-feedback", agentA, createdUtc: FixedUtcNow.AddSeconds(2)),
            Entry("run-no-feedback", agentA, createdUtc: FixedUtcNow.AddSeconds(1)),
        };
        _repository.GetRecentAcrossAgentsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<MemoryEntryEntity>>(entries));

        _feedbackService.GetFeedbackForRunAsync("run-with-feedback", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentRunFeedback>>(
                new[] { Feedback("run-with-feedback", agentA, createdBy: userX) }));
        // run-no-feedback returns empty list (default stub).

        SeedAgents((agentA, "brand-voice-auditor", "Brand Voice Auditor"));
        SeedUsers((userX, "Adam Editor"));

        var result = await _controller.GetAsync(take: null, CancellationToken.None);

        var response = (result as OkObjectResult)!.Value as MemoryWallListResponse;
        Assert.That(response, Is.Not.Null);
        Assert.That(response!.Entries, Has.Count.EqualTo(2));

        Assert.Multiple(() =>
        {
            // Row with feedback projects normally.
            var withFeedback = response.Entries.Single(e => e.RunId == "run-with-feedback");
            Assert.That(withFeedback.Score, Is.EqualTo(FeedbackScore.ThumbsDown));
            Assert.That(withFeedback.FeedbackComment, Is.EqualTo("canonical comment"));
            Assert.That(withFeedback.CreatedBy, Is.EqualTo(userX));
            Assert.That(withFeedback.CreatedByDisplayName, Is.EqualTo("Adam Editor"));

            // Row without feedback collapses Score/Comment/CreatedBy to null
            // but keeps AgentDisplayName populated (agent lookup is separate).
            var noFeedback = response.Entries.Single(e => e.RunId == "run-no-feedback");
            Assert.That(noFeedback.Score, Is.Null);
            Assert.That(noFeedback.FeedbackComment, Is.Null);
            Assert.That(noFeedback.CreatedBy, Is.Null);
            Assert.That(noFeedback.CreatedByDisplayName, Is.Null);
            Assert.That(noFeedback.AgentDisplayName, Is.EqualTo("Brand Voice Auditor"),
                "Agent display name resolves regardless of feedback presence");
        });
    }

    // ─────────────────────────────────────────────────────────────────────
    // AC7.b.iv — edge: IUserService throws
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public async Task GetAsync_UserServiceThrows_FallsBackToNullCreatedByDisplayNames()
    {
        var agentA = Guid.NewGuid();
        var userX = Guid.NewGuid();

        var entries = new[] { Entry("run-1", agentA) };
        _repository.GetRecentAcrossAgentsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<MemoryEntryEntity>>(entries));
        _feedbackService.GetFeedbackForRunAsync("run-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentRunFeedback>>(
                new[] { Feedback("run-1", agentA, createdBy: userX) }));
        SeedAgents((agentA, "brand-voice-auditor", "Brand Voice Auditor"));

        _userService.GetAsync(Arg.Any<IEnumerable<Guid>>())
            .Throws(new InvalidOperationException("simulated transient"));

        var result = await _controller.GetAsync(take: null, CancellationToken.None);

        var ok = result as OkObjectResult;
        Assert.That(ok, Is.Not.Null,
            "IUserService throw degrades gracefully — endpoint still returns 200 OK.");
        var response = ok!.Value as MemoryWallListResponse;
        Assert.That(response, Is.Not.Null);
        Assert.That(response!.Entries, Has.Count.EqualTo(1));
        // CreatedBy stays populated (came from feedback row); only the DISPLAY
        // NAME falls back to null.
        Assert.That(response.Entries[0].CreatedBy, Is.EqualTo(userX));
        Assert.That(response.Entries[0].CreatedByDisplayName, Is.Null);
        Assert.That(response.Entries[0].AgentDisplayName, Is.EqualTo("Brand Voice Auditor"),
            "Agent display-name lookup independent of user-service throw");

        _logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    // ─────────────────────────────────────────────────────────────────────
    // AC7.b.v — edge: IAIAgentService throws
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public async Task GetAsync_AgentServiceThrows_FallsBackToNullAgentDisplayNames()
    {
        var agentA = Guid.NewGuid();
        var userX = Guid.NewGuid();

        var entries = new[] { Entry("run-1", agentA) };
        _repository.GetRecentAcrossAgentsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<MemoryEntryEntity>>(entries));
        _feedbackService.GetFeedbackForRunAsync("run-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentRunFeedback>>(
                new[] { Feedback("run-1", agentA, createdBy: userX) }));
        SeedUsers((userX, "Adam Editor"));

        _agentService.GetAgentsAsync(Arg.Any<CancellationToken>())
            .Returns<Task<IEnumerable<AIAgent>>>(_ => throw new InvalidOperationException("simulated transient"));

        var result = await _controller.GetAsync(take: null, CancellationToken.None);

        var ok = result as OkObjectResult;
        Assert.That(ok, Is.Not.Null,
            "IAIAgentService throw degrades gracefully — endpoint still returns 200 OK.");
        var response = ok!.Value as MemoryWallListResponse;
        Assert.That(response, Is.Not.Null);
        Assert.That(response!.Entries, Has.Count.EqualTo(1));
        Assert.That(response.Entries[0].AgentDisplayName, Is.Null,
            "Agent display name falls back to null on upstream throw");
        Assert.That(response.Entries[0].CreatedByDisplayName, Is.EqualTo("Adam Editor"),
            "User display-name lookup independent of agent-service throw");

        _logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    // ─────────────────────────────────────────────────────────────────────
    // Code-review patch — pins AC4.b "feedback[0] = latest by CreatedUtc DESC"
    // semantic. Single-row stubs in the other tests can't catch a regression
    // that flipped to feedback.LastOrDefault() or unsorted-take.
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public async Task GetAsync_MultipleFeedbackRowsForOneRun_SurfacesFirstAsLatestPerDescOrdering()
    {
        var agentA = Guid.NewGuid();
        var olderUser = Guid.NewGuid();
        var newerUser = Guid.NewGuid();

        var entries = new[] { Entry("run-1", agentA) };
        _repository.GetRecentAcrossAgentsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<MemoryEntryEntity>>(entries));

        // Service contract orders CreatedUtc DESC — newer comes first.
        // Controller takes feedback[0] which is the newer row.
        _feedbackService.GetFeedbackForRunAsync("run-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentRunFeedback>>(new[]
            {
                Feedback("run-1", agentA, FeedbackScore.ThumbsUp,
                    "newer comment", newerUser, createdUtc: FixedUtcNow.AddMinutes(10)),
                Feedback("run-1", agentA, FeedbackScore.ThumbsDown,
                    "older comment", olderUser, createdUtc: FixedUtcNow),
            }));

        SeedAgents((agentA, "brand-voice-auditor", "Brand Voice Auditor"));
        SeedUsers(
            (olderUser, "Older Editor"),
            (newerUser, "Newer Editor"));

        var result = await _controller.GetAsync(take: null, CancellationToken.None);

        var response = (result as OkObjectResult)!.Value as MemoryWallListResponse;
        Assert.That(response, Is.Not.Null);
        Assert.That(response!.Entries, Has.Count.EqualTo(1));

        Assert.Multiple(() =>
        {
            Assert.That(response.Entries[0].Score, Is.EqualTo(FeedbackScore.ThumbsUp),
                "feedback[0] = newer row per service-layer CreatedUtc DESC contract");
            Assert.That(response.Entries[0].FeedbackComment, Is.EqualTo("newer comment"));
            Assert.That(response.Entries[0].CreatedBy, Is.EqualTo(newerUser));
            Assert.That(response.Entries[0].CreatedByDisplayName, Is.EqualTo("Newer Editor"));
        });
    }

    // ─────────────────────────────────────────────────────────────────────
    // AC7.b.vi — edge: OCE rethrow
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public void GetAsync_CancellationTokenSignalled_RethrowsOperationCanceledException()
    {
        // Mirror Story 4.8 review-patch #3 — Task.FromCanceled<T> resolves to
        // TaskCanceledException (derived from OCE), so Assert.CatchAsync
        // (derived-type tolerant) matches the production
        // `catch (OperationCanceledException)` arm. ThrowsAsync requires
        // exact-type match which would NOT match TaskCanceledException.
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var agentA = Guid.NewGuid();
        var entries = new[] { Entry("run-1", agentA) };
        _repository.GetRecentAcrossAgentsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<MemoryEntryEntity>>(entries));
        _agentService.GetAgentsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromCanceled<IEnumerable<AIAgent>>(cts.Token));

        Assert.CatchAsync<OperationCanceledException>(
            async () => await _controller.GetAsync(take: null, cts.Token));
    }
}
