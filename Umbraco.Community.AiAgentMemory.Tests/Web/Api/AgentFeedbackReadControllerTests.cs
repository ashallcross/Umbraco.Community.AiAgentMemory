using Umbraco.Community.AiAgentMemory.Composing;
using Umbraco.Community.AiAgentMemory.Feedback;
using Umbraco.Community.AiAgentMemory.Web.Api;
using Umbraco.Community.AiAgentMemory.Web.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute.ExceptionExtensions;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Services;

namespace Umbraco.Community.AiAgentMemory.Tests.Web.Api;

/// <summary>
/// Story 4.5 AC7 — tests for <see cref="AgentFeedbackReadController"/>.
/// AR30 density: happy (rows) + empty-array (NOT 404) + 400 validation +
/// composer validation. 401 not unit-tested (framework-handled by
/// <c>[Authorize]</c>; covered by manual gate AC14).
/// </summary>
[TestFixture]
public class AgentFeedbackReadControllerTests
{
    private IAgentFeedbackService _feedbackService = null!;
    private IUserService _userService = null!;
    private ILogger<AgentFeedbackReadController> _logger = null!;
    private AgentFeedbackReadController _controller = null!;

    private const string DefaultRunId = "thread-123";

    [SetUp]
    public void SetUp()
    {
        _feedbackService = Substitute.For<IAgentFeedbackService>();
        _userService = Substitute.For<IUserService>();
        // Default: empty user lookup (no display names resolved). Per-test overrides via SeedUsers.
        _userService.GetAsync(Arg.Any<IEnumerable<Guid>>())
            .Returns(Task.FromResult<IEnumerable<IUser>>(Array.Empty<IUser>()));
        _logger = Substitute.For<ILogger<AgentFeedbackReadController>>();
        _controller = new AgentFeedbackReadController(_feedbackService, _userService, _logger);
    }

    /// <summary>
    /// Seed the IUserService mock to return fake users with the supplied
    /// (key, name) pairs. Tests that want to verify display-name resolution
    /// call this in their Arrange step.
    /// </summary>
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

    [TearDown]
    public void TearDown()
    {
        _controller?.Dispose();
    }

    private static AgentRunFeedback Row(
        FeedbackScore score = FeedbackScore.ThumbsDown,
        string? comment = "canonical Northwind brand-voice comment",
        Guid? createdBy = null,
        DateTime? createdUtc = null) => new(
        Id: Guid.NewGuid(),
        RunId: DefaultRunId,
        AgentId: Guid.NewGuid(),
        Score: score,
        Comment: comment,
        CreatedBy: createdBy ?? Guid.NewGuid(),
        CreatedUtc: createdUtc ?? new DateTime(2026, 5, 19, 12, 0, 0, DateTimeKind.Utc));

    [Test]
    public async Task GetAsync_ValidRunIdWithFeedbackRows_Returns200OkWithExistingArray()
    {
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var rows = new[]
        {
            Row(score: FeedbackScore.ThumbsDown, comment: "comment A",
                createdBy: userA, createdUtc: new DateTime(2026, 5, 19, 12, 0, 0, DateTimeKind.Utc)),
            Row(score: FeedbackScore.ThumbsUp, comment: "comment B",
                createdBy: userB, createdUtc: new DateTime(2026, 5, 19, 11, 0, 0, DateTimeKind.Utc)),
        };
        _feedbackService.GetFeedbackForRunAsync(DefaultRunId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentRunFeedback>>(rows));

        var result = await _controller.GetAsync(DefaultRunId, CancellationToken.None);

        var ok = result as OkObjectResult;
        Assert.That(ok, Is.Not.Null);
        var response = ok!.Value as AgentRunFeedbackListResponse;
        Assert.That(response, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(response!.RunId, Is.EqualTo(DefaultRunId));
            Assert.That(response.Existing, Has.Count.EqualTo(2));
            Assert.That(response.Existing[0].Score, Is.EqualTo(FeedbackScore.ThumbsDown));
            Assert.That(response.Existing[0].Comment, Is.EqualTo("comment A"));
            Assert.That(response.Existing[0].CreatedBy, Is.EqualTo(userA));
            Assert.That(response.Existing[0].CreatedUtc, Is.EqualTo(new DateTime(2026, 5, 19, 12, 0, 0, DateTimeKind.Utc)));
            Assert.That(response.Existing[0].CreatedByDisplayName, Is.Null,
                "Default fixture seeds no users — display name falls back to null (widget renders 'An editor').");
        });
    }

    [Test]
    public async Task GetAsync_RowsWithKnownUsers_ResolvesDisplayNamesViaIUserService()
    {
        // DRIFT-4.5-impl-2 — IUserService batch lookup resolves the display
        // name for each row whose CreatedBy GUID is found in Umbraco's user
        // table.
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        SeedUsers((userA, "Adam Shallcross"), (userB, "Sarah Editor"));
        var rows = new[]
        {
            Row(score: FeedbackScore.ThumbsDown, comment: "comment A", createdBy: userA),
            Row(score: FeedbackScore.ThumbsUp, comment: "comment B", createdBy: userB),
        };
        _feedbackService.GetFeedbackForRunAsync(DefaultRunId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentRunFeedback>>(rows));

        var result = await _controller.GetAsync(DefaultRunId, CancellationToken.None);

        var response = (result as OkObjectResult)!.Value as AgentRunFeedbackListResponse;
        Assert.That(response, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(response!.Existing[0].CreatedByDisplayName, Is.EqualTo("Adam Shallcross"));
            Assert.That(response.Existing[1].CreatedByDisplayName, Is.EqualTo("Sarah Editor"));
        });
        // Single batch lookup (one round-trip for all distinct CreatedBy GUIDs).
        await _userService.Received(1).GetAsync(Arg.Any<IEnumerable<Guid>>());
    }

    [Test]
    public async Task GetAsync_RowsWithUnknownUser_FallsBackToNullDisplayName()
    {
        // Deleted user, never-existed manual-SQL-seeded GUID, etc. — IUserService
        // returns fewer users than supplied keys. Missing rows fall back to null.
        var unknownUser = Guid.NewGuid();
        // SeedUsers NOT called — IUserService.GetAsync returns empty.
        _feedbackService.GetFeedbackForRunAsync(DefaultRunId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentRunFeedback>>(
                new[] { Row(createdBy: unknownUser) }));

        var result = await _controller.GetAsync(DefaultRunId, CancellationToken.None);

        var response = (result as OkObjectResult)!.Value as AgentRunFeedbackListResponse;
        Assert.That(response, Is.Not.Null);
        Assert.That(response!.Existing[0].CreatedByDisplayName, Is.Null,
            "Unknown user GUID → widget falls back to 'An editor' copy.");
    }

    [Test]
    public async Task GetAsync_IUserServiceThrows_NfrR3GracefulDegradation_AllRowsFallBackToNullDisplayName()
    {
        // IUserService.GetAsync throws (DB outage, etc.) — controller catches +
        // logs Warning + returns 200 OK with null display names. Feedback rows
        // still surface; the widget just shows "An editor" for everyone.
        var userA = Guid.NewGuid();
        _userService.GetAsync(Arg.Any<IEnumerable<Guid>>())
            .ThrowsAsync(new InvalidOperationException("user service boom"));
        _feedbackService.GetFeedbackForRunAsync(DefaultRunId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentRunFeedback>>(
                new[] { Row(createdBy: userA) }));

        var result = await _controller.GetAsync(DefaultRunId, CancellationToken.None);

        var response = (result as OkObjectResult)!.Value as AgentRunFeedbackListResponse;
        Assert.That(response, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(response!.Existing, Has.Count.EqualTo(1),
                "Feedback rows still surface even when user-service lookup fails.");
            Assert.That(response.Existing[0].CreatedByDisplayName, Is.Null);
        });
        _logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Test]
    public async Task GetAsync_ValidRunIdWithZeroFeedback_Returns200OkWithEmptyExistingArray()
    {
        _feedbackService.GetFeedbackForRunAsync(DefaultRunId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentRunFeedback>>(Array.Empty<AgentRunFeedback>()));

        var result = await _controller.GetAsync(DefaultRunId, CancellationToken.None);

        var ok = result as OkObjectResult;
        Assert.That(ok, Is.Not.Null, "Empty rows → 200 OK with empty array, NEVER 404 (Story 4.5 Q1 contract).");
        var response = ok!.Value as AgentRunFeedbackListResponse;
        Assert.That(response, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(response!.RunId, Is.EqualTo(DefaultRunId));
            Assert.That(response.Existing, Is.Empty);
        });
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public async Task GetAsync_NullOrEmptyOrWhitespaceRunId_Returns400ProblemDetails(string? runId)
    {
        var result = await _controller.GetAsync(runId!, CancellationToken.None);

        var objectResult = result as ObjectResult;
        Assert.That(objectResult, Is.Not.Null);
        Assert.That(objectResult!.StatusCode, Is.EqualTo(400));
        var problem = (ProblemDetails)objectResult.Value!;
        Assert.That(problem.Detail, Does.Contain("cannot be empty"));
        // Service must NOT be called when validation fails.
        await _feedbackService.DidNotReceiveWithAnyArgs().GetFeedbackForRunAsync(default!, default);
    }

    [Test]
    public async Task GetAsync_OversizedRunId_Returns400ProblemDetails()
    {
        var oversizedRunId = new string('a', AgentFeedbackReadController.RunIdMaxChars + 1);

        var result = await _controller.GetAsync(oversizedRunId, CancellationToken.None);

        var objectResult = result as ObjectResult;
        Assert.That(objectResult, Is.Not.Null);
        Assert.That(objectResult!.StatusCode, Is.EqualTo(400));
        var problem = (ProblemDetails)objectResult.Value!;
        Assert.That(problem.Detail, Does.Contain(AgentFeedbackReadController.RunIdMaxChars.ToString()));
        await _feedbackService.DidNotReceiveWithAnyArgs().GetFeedbackForRunAsync(default!, default);
    }

    [Test]
    public void Compose_OperationIdAllowList_AgentFeedbackReadController_IsRegistered()
    {
        // Reflection-only check: the OperationIdHandler's AllowedControllers
        // array MUST include AgentFeedbackReadController, otherwise Swagger
        // raises a duplicate-operation-id boot crash. Mirrors the existing
        // composer-validation guard for AgentRunReadController.
        var composerType = typeof(AgentMemoryBackofficeApiComposer);
        var nestedHandler = composerType.GetNestedTypes(
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
        var operationIdHandlerType = nestedHandler
            .FirstOrDefault(t => t.Name == "AgentMemoryOperationIdHandler");
        Assert.That(operationIdHandlerType, Is.Not.Null,
            "Internal nested AgentMemoryOperationIdHandler type must exist.");
        var allowedField = operationIdHandlerType!.GetField(
            "AllowedControllers",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.That(allowedField, Is.Not.Null);
        var allowedTypes = (Type[])allowedField!.GetValue(null)!;
        Assert.That(allowedTypes, Does.Contain(typeof(AgentFeedbackReadController)),
            "AgentFeedbackReadController MUST be added to AllowedControllers — otherwise "
            + "Swagger raises a duplicate-operation-id boot crash.");
    }
}
