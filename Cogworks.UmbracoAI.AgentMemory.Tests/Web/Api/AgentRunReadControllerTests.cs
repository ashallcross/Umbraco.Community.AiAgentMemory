using Cogworks.UmbracoAI.AgentMemory.Runs;
using Cogworks.UmbracoAI.AgentMemory.Web.Api;
using Cogworks.UmbracoAI.AgentMemory.Web.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Cogworks.UmbracoAI.AgentMemory.Tests.Web.Api;

/// <summary>
/// Story 4.2 AC4 — tests for <see cref="AgentRunReadController"/>.
/// AR30 density: happy + 404 + null-response-snapshot + malformed-JSON.
/// 401 not unit-tested (framework-handled by <c>[Authorize]</c>; covered by
/// manual gate). Same NSubstitute-on-<c>IAgentRunReader</c> shape as
/// <c>AgentFeedbackControllerTests</c>.
/// </summary>
[TestFixture]
public class AgentRunReadControllerTests
{
    private IAgentRunReader _runReader = null!;
    private ILogger<AgentRunReadController> _logger = null!;
    private AgentRunReadController _controller = null!;
    private Guid _agentId;

    private const string DefaultRunId = "thread-123";

    [SetUp]
    public void SetUp()
    {
        _runReader = Substitute.For<IAgentRunReader>();
        _logger = Substitute.For<ILogger<AgentRunReadController>>();
        _agentId = Guid.NewGuid();

        _controller = new AgentRunReadController(_runReader, _logger);
    }

    [TearDown]
    public void TearDown()
    {
        _controller?.Dispose();
    }

    private static AgentRunRecord MakeRunRecord(
        Guid agentId,
        string? responseSnapshotJoined,
        string runId = DefaultRunId) => new(
        RunId: runId,
        AgentId: agentId,
        AgentVersion: 1,
        StartedUtc: new DateTime(2026, 5, 19, 12, 0, 0, DateTimeKind.Utc),
        CompletedUtc: new DateTime(2026, 5, 19, 12, 0, 5, DateTimeKind.Utc),
        AggregateStatus: AgentRunStatus.Succeeded,
        Error: null,
        PromptSnapshotJoined: "[user] prompt",
        ResponseSnapshotJoined: responseSnapshotJoined,
        TokenCountInput: 100,
        TokenCountOutput: 20,
        ThreadId: runId,
        UserId: "user-1",
        TraceId: "trace-1");

    // ─────────────────────────────────────────────────────────────────────
    // AC4.a — happy path (parsed structured output)
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public async Task GetAsync_ValidRunIdWithParseableStructuredOutput_Returns200WithProjectedShape()
    {
        const string responseJson = """
            {
              "score": 7,
              "issues": [
                { "text": "the wild calling", "reason": "Guideline #6 colloquialism" },
                { "text": "feet on the ground", "reason": "Guideline #6 colloquialism" }
              ],
              "suggestions": [
                "Consider rewording 'the wild calling' to direct nature description.",
                "Replace 'feet on the ground' with literal grounding language."
              ]
            }
            """;
        _runReader.GetRunsForThreadAsync(DefaultRunId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentRunRecord>>(
                new[] { MakeRunRecord(_agentId, responseJson) }));

        var result = await _controller.GetAsync(DefaultRunId, CancellationToken.None);

        var ok = result as OkObjectResult;
        Assert.That(ok, Is.Not.Null, "Happy path returns Ok(response).");
        var response = ok!.Value as AgentRunDetailResponse;
        Assert.That(response, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(response!.RunId, Is.EqualTo(DefaultRunId));
            Assert.That(response.AgentId, Is.EqualTo(_agentId));
            Assert.That(response.Score, Is.EqualTo(7));
            Assert.That(response.Issues, Has.Count.EqualTo(2));
            Assert.That(response.Issues[0].Text, Is.EqualTo("the wild calling"));
            Assert.That(response.Issues[0].Reason, Is.EqualTo("Guideline #6 colloquialism"));
            Assert.That(response.Suggestions, Has.Count.EqualTo(2));
            Assert.That(response.Suggestions[0],
                Does.StartWith("Consider rewording 'the wild calling'"));
            // v0.1: not surfaced from reader; widget falls back to "Agent {id}".
            Assert.That(response.AgentDisplayName, Is.Null);
            Assert.That(response.ContentNodeName, Is.Null);
        });
    }

    // ─────────────────────────────────────────────────────────────────────
    // AC4.b — 404 unknown runId
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public async Task GetAsync_RunReaderReturnsEmpty_Returns404ProblemDetails()
    {
        _runReader.GetRunsForThreadAsync(DefaultRunId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentRunRecord>>(Array.Empty<AgentRunRecord>()));

        var result = await _controller.GetAsync(DefaultRunId, CancellationToken.None);

        var objectResult = result as ObjectResult;
        Assert.That(objectResult, Is.Not.Null);
        Assert.That(objectResult!.StatusCode, Is.EqualTo(404));
        Assert.That(objectResult.Value, Is.InstanceOf<ProblemDetails>());
        var problem = (ProblemDetails)objectResult.Value!;
        Assert.That(problem.Title, Is.EqualTo("Run not found."));
        // Adopter-facing 404 copy (Story 4.2 § Review Findings patch #9 —
        // dropped internal "PR-Upstream-N / Fork (i)" jargon in favour of a
        // refresh-and-retry instruction).
        Assert.That(problem.Detail, Does.Contain("not be audit-logged yet"));
    }

    // ─────────────────────────────────────────────────────────────────────
    // AC4 (post-review patch #5) — runId length guard
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public async Task GetAsync_RunIdExceeds256Chars_Returns400ProblemDetails()
    {
        var oversizedRunId = new string('a', 257);

        var result = await _controller.GetAsync(oversizedRunId, CancellationToken.None);

        var objectResult = result as ObjectResult;
        Assert.That(objectResult, Is.Not.Null);
        Assert.That(objectResult!.StatusCode, Is.EqualTo(400));
        var problem = (ProblemDetails)objectResult.Value!;
        Assert.That(problem.Detail, Does.Contain("256"));
        // Reader must NOT be called when the input fails validation.
        await _runReader.DidNotReceiveWithAnyArgs().GetRunsForThreadAsync(default!, default);
    }

    // ─────────────────────────────────────────────────────────────────────
    // AC4 (post-review patch #3) — score emitted as decimal-number is parsed
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public async Task GetAsync_ScoreEmittedAsDecimalNumber_RoundsToInt()
    {
        // LLMs sometimes emit integer-valued scores as 7.0 (or 7.5 in
        // continuous-rating modes). TryGetInt32 rejects these; the
        // double-fallback should round to the nearest int.
        const string responseJson = """[assistant] {"score":7.5,"issues":[{"text":"x"}],"suggestions":["s"]}""";
        _runReader.GetRunsForThreadAsync(DefaultRunId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentRunRecord>>(
                new[] { MakeRunRecord(_agentId, responseJson) }));

        var result = await _controller.GetAsync(DefaultRunId, CancellationToken.None);

        var response = (result as OkObjectResult)!.Value as AgentRunDetailResponse;
        Assert.That(response, Is.Not.Null);
        Assert.That(response!.Score, Is.EqualTo(8), "7.5 rounds to 8 (Math.Round banker's rounding).");
    }

    // ─────────────────────────────────────────────────────────────────────
    // AC4 (post-review patch #4) — issues / suggestions array capped at 100
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public async Task GetAsync_IssuesArrayExceeds100Items_CapsAt100()
    {
        // Build a 150-item issues array. Parser must cap pre-allocation +
        // iteration at MaxStructuredOutputItems (100) so attacker-controlled
        // JSON can't OOM via runaway list capacity.
        var issues = string.Join(",", Enumerable.Range(0, 150)
            .Select(i => $$"""{"text":"issue {{i}}","reason":"r"}"""));
        var responseJson = $$"""[assistant] {"score":5,"issues":[{{issues}}],"suggestions":[]}""";
        _runReader.GetRunsForThreadAsync(DefaultRunId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentRunRecord>>(
                new[] { MakeRunRecord(_agentId, responseJson) }));

        var result = await _controller.GetAsync(DefaultRunId, CancellationToken.None);

        var response = (result as OkObjectResult)!.Value as AgentRunDetailResponse;
        Assert.That(response!.Issues, Has.Count.EqualTo(100));
        Assert.That(response.Issues[0].Text, Is.EqualTo("issue 0"));
        Assert.That(response.Issues[99].Text, Is.EqualTo("issue 99"));
    }

    // ─────────────────────────────────────────────────────────────────────
    // AC4.c — graceful degradation on null ResponseSnapshotJoined
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public async Task GetAsync_NullResponseSnapshot_Returns200WithEmptyStructuredFields()
    {
        _runReader.GetRunsForThreadAsync(DefaultRunId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentRunRecord>>(
                new[] { MakeRunRecord(_agentId, responseSnapshotJoined: null) }));

        var result = await _controller.GetAsync(DefaultRunId, CancellationToken.None);

        var ok = result as OkObjectResult;
        Assert.That(ok, Is.Not.Null);
        var response = ok!.Value as AgentRunDetailResponse;
        Assert.That(response, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(response!.RunId, Is.EqualTo(DefaultRunId), "Identity fields populated even when structured output is absent.");
            Assert.That(response.AgentId, Is.EqualTo(_agentId));
            Assert.That(response.Score, Is.Null);
            Assert.That(response.Issues, Is.Empty);
            Assert.That(response.Suggestions, Is.Empty);
        });
    }

    // ─────────────────────────────────────────────────────────────────────
    // AC4.e — DRIFT-4.2-impl-2: ResponseSnapshotJoined is a transcript, not
    // raw JSON. Parser must locate the [assistant] tag and extract JSON.
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public async Task GetAsync_TranscriptShapeWithToolCallRoundsBeforeAssistant_ExtractsStructuredOutputFromFinalAssistantTurn()
    {
        // Empirical shape observed at Story 4.2 manual gate dry-run 2026-05-19
        // against Run ID 347c2071. Multi-round transcript: tool_call + tool +
        // assistant. Parser must use LastIndexOf("[assistant]") to skip
        // intermediate rounds and parse the final turn's structured output.
        const string transcript = """
              [tool_call:toolu_01ABC] list_context_resources({"args":{}})
            [tool:toolu_01ABC] -> {"resources":[],"message":"No on-demand context resources are available."}
            [assistant] {"score":7,"issues":[{"text":"The wild calling","reason":"Guideline #6 colloquialism"},{"text":"feet on the ground","reason":"Guideline #6"},{"text":"the long way home","reason":"Guideline #6"}],"suggestions":["Reword 'wild calling' to direct description.","Drop 'feet on the ground' for literal grounding."]}
            """;
        _runReader.GetRunsForThreadAsync(DefaultRunId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentRunRecord>>(
                new[] { MakeRunRecord(_agentId, transcript) }));

        var result = await _controller.GetAsync(DefaultRunId, CancellationToken.None);

        var ok = result as OkObjectResult;
        Assert.That(ok, Is.Not.Null);
        var response = ok!.Value as AgentRunDetailResponse;
        Assert.That(response, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(response!.Score, Is.EqualTo(7),
                "Parser must extract score from the [assistant]-tag JSON payload, not the tool-call/tool JSON in earlier rounds.");
            Assert.That(response.Issues, Has.Count.EqualTo(3));
            Assert.That(response.Issues[0].Text, Is.EqualTo("The wild calling"));
            Assert.That(response.Suggestions, Has.Count.EqualTo(2));
        });
    }

    [Test]
    public async Task GetAsync_TranscriptWithoutAssistantTag_FallsBackToWholeDocumentParse()
    {
        // Future-proof seam: adopter agents that don't emit transcript-format
        // ResponseSnapshot — the parser falls back to whole-doc JSON.Parse so
        // raw-JSON adopter shapes still work.
        const string rawJson = """{"score":8,"issues":[{"text":"raw"}],"suggestions":[]}""";
        _runReader.GetRunsForThreadAsync(DefaultRunId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentRunRecord>>(
                new[] { MakeRunRecord(_agentId, rawJson) }));

        var result = await _controller.GetAsync(DefaultRunId, CancellationToken.None);

        var ok = result as OkObjectResult;
        var response = ok!.Value as AgentRunDetailResponse;
        Assert.That(response, Is.Not.Null);
        Assert.That(response!.Score, Is.EqualTo(8));
        Assert.That(response.Issues, Has.Count.EqualTo(1));
    }

    // ─────────────────────────────────────────────────────────────────────
    // AC4.d — graceful degradation on malformed JSON
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public async Task GetAsync_MalformedJsonResponseSnapshot_Returns200WithEmptyStructuredFields()
    {
        // Plain text, not JSON — TryParseStructuredOutput catches JsonException
        // and returns the empty-fields shape.
        const string malformed = "Agent response is not JSON-formatted text.";
        _runReader.GetRunsForThreadAsync(DefaultRunId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentRunRecord>>(
                new[] { MakeRunRecord(_agentId, malformed) }));

        var result = await _controller.GetAsync(DefaultRunId, CancellationToken.None);

        var ok = result as OkObjectResult;
        Assert.That(ok, Is.Not.Null,
            "Malformed JSON degrades gracefully — endpoint still returns 200 OK so the widget can render the feedback form.");
        var response = ok!.Value as AgentRunDetailResponse;
        Assert.That(response, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(response!.Score, Is.Null);
            Assert.That(response.Issues, Is.Empty);
            Assert.That(response.Suggestions, Is.Empty);
        });
    }
}
