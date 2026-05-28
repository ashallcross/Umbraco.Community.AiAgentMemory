using Cogworks.UmbracoAI.AgentMemory.Feedback;
using Cogworks.UmbracoAI.AgentMemory.Memory;
using Cogworks.UmbracoAI.AgentMemory.Runs;
using Cogworks.UmbracoAI.AgentMemory.Web.Api;
using Cogworks.UmbracoAI.AgentMemory.Web.Api.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Security;

namespace Cogworks.UmbracoAI.AgentMemory.Tests.Web.Api;

/// <summary>
/// Story 2.2 + Story 2.3 Task 0.6 amendment — AgentFeedbackController tests. The
/// controller's request body shape moved from 4 fields (runId/agentId/score/comment)
/// to 3 fields (runId/score/comment); <c>agentId</c> is now resolved server-side
/// via <see cref="IAgentRunReader.GetRunsForThreadAsync"/>.
/// </summary>
[TestFixture]
public class AgentFeedbackControllerTests
{
    private IAgentFeedbackService _feedbackService = null!;
    private IFeedbackIndexer _indexer = null!;
    private IBackOfficeSecurityAccessor _securityAccessor = null!;
    private IAgentRunReader _runReader = null!;
    private ILogger<AgentFeedbackController> _logger = null!;
    private AgentFeedbackController _controller = null!;
    private Guid _resolvedUserKey;
    private Guid _agentId;

    [SetUp]
    public void SetUp()
    {
        _feedbackService = Substitute.For<IAgentFeedbackService>();
        _indexer = Substitute.For<IFeedbackIndexer>();
        _securityAccessor = Substitute.For<IBackOfficeSecurityAccessor>();
        _runReader = Substitute.For<IAgentRunReader>();
        _logger = Substitute.For<ILogger<AgentFeedbackController>>();

        // Default: authenticated user with a valid GUID.
        var security = Substitute.For<IBackOfficeSecurity>();
        var user = Substitute.For<IUser>();
        _resolvedUserKey = Guid.NewGuid();
        user.Key.Returns(_resolvedUserKey);
        security.CurrentUser.Returns(user);
        _securityAccessor.BackOfficeSecurity.Returns(security);

        _agentId = Guid.NewGuid();

        // Default: reader returns a single matching AgentRunRecord carrying
        // _agentId. Story 2.3 Task 0.6 — controller resolves agentId via this
        // lookup instead of trusting a client-supplied value.
        _runReader.GetRunsForThreadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentRunRecord>>(
                new[] { MakeRunRecord(_agentId) }));

        _controller = new AgentFeedbackController(
            _feedbackService, _indexer, _securityAccessor, _runReader, _logger);
    }

    [TearDown]
    public void TearDown()
    {
        _controller?.Dispose();
    }

    private static AgentRunRecord MakeRunRecord(Guid agentId, string runId = "run-1") => new(
        RunId: runId,
        AgentId: agentId,
        AgentVersion: 1,
        StartedUtc: DateTime.UtcNow.AddMinutes(-1),
        CompletedUtc: DateTime.UtcNow,
        AggregateStatus: AgentRunStatus.Succeeded,
        Error: null,
        PromptSnapshotJoined: "[user] hello",
        ResponseSnapshotJoined: "[assistant] hi",
        TokenCountInput: 100,
        TokenCountOutput: 20,
        ThreadId: runId,
        UserId: "user-1",
        TraceId: "trace-1");

    [Test]
    public async Task PostAsync_HappyPath_ResolvesAgentIdFromReader_CallsServiceWithResolvedHostUserGuid()
    {
        var request = new AgentFeedbackPostRequest(
            RunId: "run-1",
            Score: FeedbackScore.ThumbsDown,
            Comment: "actually wrong");

        var result = await _controller.PostAsync(request, CancellationToken.None);

        Assert.That(result, Is.InstanceOf<OkResult>(),
            "Happy path returns Ok() (HTTP 200, empty body) per AC4.");

        // Reader was queried for the supplied runId (= upstream ThreadId).
        await _runReader.Received(1).GetRunsForThreadAsync("run-1", Arg.Any<CancellationToken>());

        // Service was called with agentId resolved from the reader (NOT a
        // client-supplied value — the request body no longer carries it).
        await _feedbackService.Received(1).RecordFeedbackAsync(
            "run-1",
            _agentId,
            FeedbackScore.ThumbsDown,
            "actually wrong",
            _resolvedUserKey,
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task PostAsync_RecordsFeedbackThenEnqueuesIndexer_InOrder()
    {
        // Story 3.1 AC7 + AC1 — the controller enqueues indexing AFTER the
        // service write succeeds. Ordering matters: the indexer reads the
        // feedback row via IAgentFeedbackService.GetFeedbackForRunAsync, so
        // the row must be persisted first.
        var request = new AgentFeedbackPostRequest(
            RunId: "run-1",
            Score: FeedbackScore.ThumbsUp,
            Comment: "looks good");

        var result = await _controller.PostAsync(request, CancellationToken.None);

        Assert.That(result, Is.InstanceOf<OkResult>());

        Received.InOrder(() =>
        {
            _feedbackService.RecordFeedbackAsync(
                "run-1",
                _agentId,
                FeedbackScore.ThumbsUp,
                "looks good",
                _resolvedUserKey,
                Arg.Any<CancellationToken>());
            _indexer.EnqueueIndex("run-1", _agentId);
        });
    }

    [Test]
    public async Task PostAsync_RunIdNotFoundInReader_Returns404ProblemDetails_NoServiceCall()
    {
        // Story 2.3 Task 0.6 — new 404 path. The reader returns empty when no
        // AIAuditLog row matches the supplied runId (= upstream ThreadId). This
        // happens when (a) the audit row hasn't been written yet (race window
        // between agent completion and ASP.NET-Core-emitted feedback POST), OR
        // (b) the upstream Fork (i) metadata propagation isn't deployed.
        _runReader.GetRunsForThreadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentRunRecord>>(Array.Empty<AgentRunRecord>()));

        var request = new AgentFeedbackPostRequest("run-missing", FeedbackScore.ThumbsUp, null);

        var result = await _controller.PostAsync(request, CancellationToken.None);

        Assert.That(result, Is.InstanceOf<ObjectResult>());
        var objectResult = (ObjectResult)result;
        Assert.That(objectResult.StatusCode, Is.EqualTo(StatusCodes.Status404NotFound));
        Assert.That(objectResult.Value, Is.InstanceOf<ProblemDetails>());
        var problem = (ProblemDetails)objectResult.Value!;
        Assert.That(problem.Title, Is.EqualTo("Run not found."));
        Assert.That(problem.Detail, Does.Contain("run-missing"));

        // Service is NEVER called when the run can't be resolved.
        await _feedbackService.DidNotReceiveWithAnyArgs().RecordFeedbackAsync(
            default!, default, default, default, default, default);
    }

    private static IEnumerable<TestCaseData> InvalidPayloadCases()
    {
        // AC5 — validation rules, first-failure-return semantics. Story 2.3
        // Task 0.6 removes the Guid.Empty AgentId case (field dropped from POCO).
        yield return new TestCaseData(
            (AgentFeedbackPostRequest?)null,
            "Request body is required.");
        yield return new TestCaseData(
            new AgentFeedbackPostRequest(string.Empty, FeedbackScore.ThumbsUp, null),
            "runId is required");
        yield return new TestCaseData(
            new AgentFeedbackPostRequest(new string('x', AgentFeedbackController.RunIdMaxChars + 1), FeedbackScore.ThumbsUp, null),
            "runId cannot exceed 256");
        yield return new TestCaseData(
            new AgentFeedbackPostRequest("run-1", (FeedbackScore)99, null),
            "score must be 0");
        yield return new TestCaseData(
            new AgentFeedbackPostRequest("run-1", FeedbackScore.ThumbsUp, new string('x', AgentFeedbackController.CommentMaxChars + 1)),
            "comment cannot exceed 4000");
    }

    [TestCaseSource(nameof(InvalidPayloadCases))]
    public async Task PostAsync_InvalidPayload_Returns400ProblemDetails_PinsValidationContract(
        AgentFeedbackPostRequest? request,
        string expectedDetailContains)
    {
        var result = await _controller.PostAsync(request, CancellationToken.None);

        Assert.That(result, Is.InstanceOf<ObjectResult>(),
            "Validation failures return ProblemDetails via ObjectResult per AC5.");
        var objectResult = (ObjectResult)result;
        Assert.That(objectResult.StatusCode, Is.EqualTo(StatusCodes.Status400BadRequest));
        Assert.That(objectResult.Value, Is.InstanceOf<ProblemDetails>());
        var problem = (ProblemDetails)objectResult.Value!;
        Assert.That(problem.Detail, Does.Contain(expectedDetailContains),
            $"ProblemDetails.Detail should contain '{expectedDetailContains}' for this validation rule.");

        // Service is NEVER called when validation fails (first-failure return).
        await _feedbackService.DidNotReceiveWithAnyArgs().RecordFeedbackAsync(
            default!, default, default, default, default, default);
        // Reader is NEVER called either — validation short-circuits before lookup.
        await _runReader.DidNotReceiveWithAnyArgs().GetRunsForThreadAsync(
            default!, default);
    }

    [Test]
    public async Task PostAsync_NullSecurityCurrentUser_Returns401_NoServiceCall()
    {
        // Defense-in-depth (AC6) — hop 1: if BackOfficeSecurity itself is null
        // (config drift), the controller returns 401 and the service is never
        // called.
        _securityAccessor.BackOfficeSecurity.Returns((IBackOfficeSecurity?)null);

        var request = new AgentFeedbackPostRequest("run-1", FeedbackScore.ThumbsUp, null);

        var result = await _controller.PostAsync(request, CancellationToken.None);

        var objectResult = result as ObjectResult;
        Assert.That(objectResult, Is.Not.Null,
            "Defense-in-depth 401 returns ProblemDetails via ObjectResult.");
        Assert.That(objectResult!.StatusCode, Is.EqualTo(StatusCodes.Status401Unauthorized));

        await _feedbackService.DidNotReceiveWithAnyArgs().RecordFeedbackAsync(
            default!, default, default, default, default, default);

        AssertLoggedWarningOnce();
    }

    [Test]
    public async Task PostAsync_NullCurrentUser_Returns401_NoServiceCall()
    {
        // Defense-in-depth (AC6) — hop 2 (middle null hop): BackOfficeSecurity
        // resolves but CurrentUser is null. The controller's
        // ?.BackOfficeSecurity?.CurrentUser?.Key chain has 3 hops; hop 1 + 3
        // are pinned by the surrounding tests, hop 2 is pinned here.
        var emptySecurity = Substitute.For<IBackOfficeSecurity>();
        emptySecurity.CurrentUser.Returns((IUser?)null);
        _securityAccessor.BackOfficeSecurity.Returns(emptySecurity);

        var request = new AgentFeedbackPostRequest("run-1", FeedbackScore.ThumbsUp, null);

        var result = await _controller.PostAsync(request, CancellationToken.None);

        var objectResult = result as ObjectResult;
        Assert.That(objectResult, Is.Not.Null);
        Assert.That(objectResult!.StatusCode, Is.EqualTo(StatusCodes.Status401Unauthorized));

        await _feedbackService.DidNotReceiveWithAnyArgs().RecordFeedbackAsync(
            default!, default, default, default, default, default);

        AssertLoggedWarningOnce();
    }

    [Test]
    public async Task PostAsync_GuidEmptyCurrentUserKey_Returns401_NoServiceCall()
    {
        // NFR-S7 defense-in-depth (AC6) — hop 3 (leaf): a CurrentUser with
        // Guid.Empty Key MUST NOT be persisted as createdBy. The auth filter
        // should have blocked this; if it didn't, the controller catches it.
        var emptyUser = Substitute.For<IUser>();
        emptyUser.Key.Returns(Guid.Empty);
        var emptySecurity = Substitute.For<IBackOfficeSecurity>();
        emptySecurity.CurrentUser.Returns(emptyUser);
        _securityAccessor.BackOfficeSecurity.Returns(emptySecurity);

        var request = new AgentFeedbackPostRequest("run-1", FeedbackScore.ThumbsUp, null);

        var result = await _controller.PostAsync(request, CancellationToken.None);

        var objectResult = result as ObjectResult;
        Assert.That(objectResult, Is.Not.Null);
        Assert.That(objectResult!.StatusCode, Is.EqualTo(StatusCodes.Status401Unauthorized));

        await _feedbackService.DidNotReceiveWithAnyArgs().RecordFeedbackAsync(
            default!, default, default, default, default, default);

        AssertLoggedWarningOnce();
    }

    private void AssertLoggedWarningOnce()
    {
        // Spec Task 6e — pin that the controller emits ILogger.LogWarning(...)
        // when the host user identity cannot be resolved. NSubstitute verifies
        // on the underlying ILogger.Log<TState>(...) extension dispatch.
        _logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Test]
    public void PostAsync_PropagatesOperationCanceledExceptionUnwrapped()
    {
        // Story 1.2 / 2.1 carry-forward: OperationCanceledException MUST
        // propagate verbatim through the controller (NOT swallowed, NOT wrapped).
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        _feedbackService.RecordFeedbackAsync(
                Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<FeedbackScore>(),
                Arg.Any<string?>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new OperationCanceledException()));

        var request = new AgentFeedbackPostRequest("run-1", FeedbackScore.ThumbsUp, null);

        Assert.ThrowsAsync<OperationCanceledException>(
            () => _controller.PostAsync(request, cts.Token));
    }

    [Test]
    public void PostAsync_ServiceThrowsNonCancellationException_BubblesAs500ViaFramework()
    {
        // AC4 / NFR-R3 asymmetric application (carry-forward from Story 2.1):
        // write-path exceptions propagate UNWRAPPED to the framework. The
        // controller does NOT catch — the framework's exception filter converts
        // the throw into HTTP 500. This test pins the no-catch contract.
        _feedbackService.RecordFeedbackAsync(
                Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<FeedbackScore>(),
                Arg.Any<string?>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("DB unreachable")));

        var request = new AgentFeedbackPostRequest("run-1", FeedbackScore.ThumbsUp, null);

        Assert.ThrowsAsync<InvalidOperationException>(
            () => _controller.PostAsync(request, CancellationToken.None));
    }

    // ═════════════════════════════════════════════════════════════════════
    // Story 4.12 — selectedRunId picker path (AC4.1; AC5 controller tests)
    // ═════════════════════════════════════════════════════════════════════

    [Test]
    public async Task PostAsync_SelectedRunIdSupplied_RecordsAgainstSelectedRunId_NotThreadId()
    {
        // Batch picker submission: threadId = "thread-A"; user picks iteration
        // "rid-step-2" out of 3 siblings. Controller must record feedback under
        // RunId="rid-step-2" + enqueue indexer with the same per-iteration RunId.
        // Each sibling's AgentRunRecord carries the SAME agentId (single-agent
        // workflow) — the controller resolves agentId from the selected sibling.
        var siblingAgentId = Guid.NewGuid();
        var siblings = new[]
        {
            MakeRunRecord(siblingAgentId, "rid-step-3"),
            MakeRunRecord(siblingAgentId, "rid-step-2"),
            MakeRunRecord(siblingAgentId, "rid-step-1"),
        };
        _runReader.GetRunsForThreadAsync("thread-A", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentRunRecord>>(siblings));

        var request = new AgentFeedbackPostRequest(
            RunId: "thread-A",
            Score: FeedbackScore.ThumbsDown,
            Comment: "iteration-specific teaching",
            SelectedRunId: "rid-step-2");

        var result = await _controller.PostAsync(request, CancellationToken.None);

        Assert.That(result, Is.InstanceOf<OkResult>(),
            "Picker submission with a valid selectedRunId returns Ok.");

        await _feedbackService.Received(1).RecordFeedbackAsync(
            "rid-step-2",                       // feedbackRunId = selectedRunId
            siblingAgentId,
            FeedbackScore.ThumbsDown,
            "iteration-specific teaching",
            _resolvedUserKey,
            Arg.Any<CancellationToken>());

        // Indexer enqueued with the per-iteration RunId so FeedbackIndexer
        // embeds THAT iteration's prompt/response, not runs[0]'s.
        _indexer.Received(1).EnqueueIndex("rid-step-2", siblingAgentId);

        // The ThreadId-keyed enqueue path MUST NOT fire on picker submissions
        // — otherwise we'd index against the legacy ThreadId row in addition
        // to (or instead of) the selected iteration.
        _indexer.DidNotReceive().EnqueueIndex("thread-A", Arg.Any<Guid>());

        // P19 — pin total call count so a future regression that fires
        // EnqueueIndex twice (or under unexpected args) is caught.
        Assert.That(
            _indexer.ReceivedCalls().Count(c => c.GetMethodInfo().Name == "EnqueueIndex"),
            Is.EqualTo(1),
            "EnqueueIndex must fire exactly once per picker submission.");
    }

    [Test]
    public async Task PostAsync_SelectedRunIdNotFoundInSiblingGroup_Returns404_NoServiceCall()
    {
        var siblings = new[] { MakeRunRecord(Guid.NewGuid(), "rid-step-1") };
        _runReader.GetRunsForThreadAsync("thread-A", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentRunRecord>>(siblings));

        var request = new AgentFeedbackPostRequest(
            RunId: "thread-A",
            Score: FeedbackScore.ThumbsUp,
            Comment: null,
            SelectedRunId: "rid-does-not-exist");

        var result = await _controller.PostAsync(request, CancellationToken.None);

        var objectResult = result as ObjectResult;
        Assert.That(objectResult, Is.Not.Null);
        Assert.That(objectResult!.StatusCode, Is.EqualTo(StatusCodes.Status404NotFound));
        var problem = (ProblemDetails)objectResult.Value!;
        Assert.That(problem.Title, Is.EqualTo("Selected iteration not found."));

        await _feedbackService.DidNotReceiveWithAnyArgs().RecordFeedbackAsync(
            default!, default, default, default, default, default);
        _indexer.DidNotReceiveWithAnyArgs().EnqueueIndex(default!, default);
    }

    [Test]
    public async Task PostAsync_SelectedRunIdOmitted_PreservesPreStory412ThreadIdKeyedBehaviour()
    {
        // Byte-compatibility pin for Story 2.3 + Story 4.5 — when SelectedRunId
        // is null, the controller records feedback under the ThreadId-shaped
        // RunId field exactly as before. Tests must verify the LEGACY path
        // didn't accidentally switch to selectedRunId-keyed.
        //
        // P17 — explicit `SelectedRunId: null` rather than relying on the
        // record default; pins the legacy-mode contract against future default-
        // value changes.
        var request = new AgentFeedbackPostRequest(
            RunId: "thread-A",
            Score: FeedbackScore.ThumbsUp,
            Comment: "legacy submission",
            SelectedRunId: null);

        var result = await _controller.PostAsync(request, CancellationToken.None);

        Assert.That(result, Is.InstanceOf<OkResult>());

        // Records against the ThreadId-shaped RunId, using runs[0].AgentId.
        await _feedbackService.Received(1).RecordFeedbackAsync(
            "thread-A",
            _agentId,
            FeedbackScore.ThumbsUp,
            "legacy submission",
            _resolvedUserKey,
            Arg.Any<CancellationToken>());
        _indexer.Received(1).EnqueueIndex("thread-A", _agentId);
    }

    [Test]
    public async Task PostAsync_SelectedRunIdExceeds256Chars_Returns400_NoServiceCall()
    {
        var request = new AgentFeedbackPostRequest(
            RunId: "thread-A",
            Score: FeedbackScore.ThumbsUp,
            Comment: null,
            SelectedRunId: new string('a', AgentFeedbackController.RunIdMaxChars + 1));

        var result = await _controller.PostAsync(request, CancellationToken.None);

        var objectResult = result as ObjectResult;
        Assert.That(objectResult, Is.Not.Null);
        Assert.That(objectResult!.StatusCode, Is.EqualTo(StatusCodes.Status400BadRequest));
        var problem = (ProblemDetails)objectResult.Value!;
        Assert.That(problem.Detail, Does.Contain("selectedRunId"));

        await _feedbackService.DidNotReceiveWithAnyArgs().RecordFeedbackAsync(
            default!, default, default, default, default, default);
    }

    [TestCase("   ")]
    [TestCase("\t")]
    [TestCase("")]
    public async Task PostAsync_SelectedRunIdWhitespaceOnly_Returns400_NoServiceCall(string whitespaceValue)
    {
        // P12 — symmetric with the RunId whitespace check. Silently treating
        // whitespace-only as "legacy mode" would mask client contract drift;
        // surface explicitly as 400.
        var request = new AgentFeedbackPostRequest(
            RunId: "thread-A",
            Score: FeedbackScore.ThumbsUp,
            Comment: null,
            SelectedRunId: whitespaceValue);

        var result = await _controller.PostAsync(request, CancellationToken.None);

        var objectResult = result as ObjectResult;
        Assert.That(objectResult, Is.Not.Null);
        Assert.That(objectResult!.StatusCode, Is.EqualTo(StatusCodes.Status400BadRequest));
        var problem = (ProblemDetails)objectResult.Value!;
        Assert.That(problem.Detail, Does.Contain("selectedRunId"));

        await _feedbackService.DidNotReceiveWithAnyArgs().RecordFeedbackAsync(
            default!, default, default, default, default, default);
        _indexer.DidNotReceiveWithAnyArgs().EnqueueIndex(default!, default);
    }

    [Test]
    public async Task PostAsync_SelectedRunIdMatchesMultipleSiblings_PicksFirst_LogsWarning()
    {
        // P3 — single-row-per-RunId contract is documented but not enforced
        // by the audit-log writer; a future tool-call follow-up retry that
        // re-uses a RunId would otherwise crash with SingleOrDefault throwing.
        // Verify the FirstOrDefault + warn-log fallback.
        var agentId = Guid.NewGuid();
        var siblings = new[]
        {
            MakeRunRecord(agentId, "rid-step-2"),  // duplicate
            MakeRunRecord(agentId, "rid-step-2"),  // duplicate
            MakeRunRecord(agentId, "rid-step-1"),
        };
        _runReader.GetRunsForThreadAsync("thread-A", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentRunRecord>>(siblings));

        var request = new AgentFeedbackPostRequest(
            RunId: "thread-A",
            Score: FeedbackScore.ThumbsUp,
            Comment: "first-match",
            SelectedRunId: "rid-step-2");

        var result = await _controller.PostAsync(request, CancellationToken.None);

        Assert.That(result, Is.InstanceOf<OkResult>(),
            "Picker submission with duplicate RunIds succeeds against the first match.");

        await _feedbackService.Received(1).RecordFeedbackAsync(
            "rid-step-2",
            agentId,
            FeedbackScore.ThumbsUp,
            "first-match",
            _resolvedUserKey,
            Arg.Any<CancellationToken>());

        _logger.Received().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("single-row-per-RunId contract violated")),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }
}
