using Cogworks.UmbracoAI.AgentMemory.Feedback;
using Cogworks.UmbracoAI.AgentMemory.Web.Api;
using Cogworks.UmbracoAI.AgentMemory.Web.Api.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Security;

namespace Cogworks.UmbracoAI.AgentMemory.Tests.Web.Api;

/// <summary>
/// Story 2.2 — AgentFeedbackController tests. 6 controller-layer methods
/// (one parameterised) + 2 composer-layer extensions live in
/// <see cref="Composing.AgentMemoryComposerStartupValidationTests"/>.
/// </summary>
[TestFixture]
public class AgentFeedbackControllerTests
{
    private IAgentFeedbackService _feedbackService = null!;
    private IBackOfficeSecurityAccessor _securityAccessor = null!;
    private ILogger<AgentFeedbackController> _logger = null!;
    private AgentFeedbackController _controller = null!;
    private Guid _resolvedUserKey;
    private Guid _agentId;

    [SetUp]
    public void SetUp()
    {
        _feedbackService = Substitute.For<IAgentFeedbackService>();
        _securityAccessor = Substitute.For<IBackOfficeSecurityAccessor>();
        _logger = Substitute.For<ILogger<AgentFeedbackController>>();

        // Default: authenticated user with a valid GUID.
        var security = Substitute.For<IBackOfficeSecurity>();
        var user = Substitute.For<IUser>();
        _resolvedUserKey = Guid.NewGuid();
        user.Key.Returns(_resolvedUserKey);
        security.CurrentUser.Returns(user);
        _securityAccessor.BackOfficeSecurity.Returns(security);

        _agentId = Guid.NewGuid();

        _controller = new AgentFeedbackController(_feedbackService, _securityAccessor, _logger);
    }

    [TearDown]
    public void TearDown()
    {
        _controller?.Dispose();
    }

    [Test]
    public async Task PostAsync_HappyPath_CallsServiceWithResolvedHostUserGuid()
    {
        var request = new AgentFeedbackPostRequest(
            RunId: "run-1",
            AgentId: _agentId,
            Score: FeedbackScore.ThumbsDown,
            Comment: "actually wrong");

        var result = await _controller.PostAsync(request, CancellationToken.None);

        Assert.That(result, Is.InstanceOf<OkResult>(),
            "Happy path returns Ok() (HTTP 200, empty body) per AC4.");

        await _feedbackService.Received(1).RecordFeedbackAsync(
            "run-1",
            _agentId,
            FeedbackScore.ThumbsDown,
            "actually wrong",
            _resolvedUserKey,
            Arg.Any<CancellationToken>());
    }

    private static IEnumerable<TestCaseData> InvalidPayloadCases()
    {
        // AC5 — six validation rules, first-failure-return semantics.
        yield return new TestCaseData(
            (AgentFeedbackPostRequest?)null,
            "Request body is required.");
        yield return new TestCaseData(
            new AgentFeedbackPostRequest(string.Empty, Guid.NewGuid(), FeedbackScore.ThumbsUp, null),
            "runId is required");
        yield return new TestCaseData(
            new AgentFeedbackPostRequest(new string('x', AgentFeedbackController.RunIdMaxChars + 1), Guid.NewGuid(), FeedbackScore.ThumbsUp, null),
            "runId cannot exceed 256");
        yield return new TestCaseData(
            new AgentFeedbackPostRequest("run-1", Guid.Empty, FeedbackScore.ThumbsUp, null),
            "agentId is required and cannot be Guid.Empty");
        yield return new TestCaseData(
            new AgentFeedbackPostRequest("run-1", Guid.NewGuid(), (FeedbackScore)99, null),
            "score must be 0");
        yield return new TestCaseData(
            new AgentFeedbackPostRequest("run-1", Guid.NewGuid(), FeedbackScore.ThumbsUp, new string('x', AgentFeedbackController.CommentMaxChars + 1)),
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
    }

    [Test]
    public async Task PostAsync_NullSecurityCurrentUser_Returns401_NoServiceCall()
    {
        // Defense-in-depth (AC6) — hop 1: if BackOfficeSecurity itself is null
        // (config drift), the controller returns 401 and the service is never
        // called.
        _securityAccessor.BackOfficeSecurity.Returns((IBackOfficeSecurity?)null);

        var request = new AgentFeedbackPostRequest("run-1", _agentId, FeedbackScore.ThumbsUp, null);

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

        var request = new AgentFeedbackPostRequest("run-1", _agentId, FeedbackScore.ThumbsUp, null);

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

        var request = new AgentFeedbackPostRequest("run-1", _agentId, FeedbackScore.ThumbsUp, null);

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

        var request = new AgentFeedbackPostRequest("run-1", _agentId, FeedbackScore.ThumbsUp, null);

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

        var request = new AgentFeedbackPostRequest("run-1", _agentId, FeedbackScore.ThumbsUp, null);

        Assert.ThrowsAsync<InvalidOperationException>(
            () => _controller.PostAsync(request, CancellationToken.None));
    }
}
