using Cogworks.UmbracoAI.AgentMemory.Runs;
using Cogworks.UmbracoAI.AgentMemory.Web.Api;
using Cogworks.UmbracoAI.AgentMemory.Web.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Umbraco.AI.Agent.Core.Agents;

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
    private IAIAgentService _agentService = null!;
    private ILogger<AgentRunReadController> _logger = null!;
    private AgentRunReadController _controller = null!;
    private Guid _agentId;

    private const string DefaultRunId = "thread-123";

    [SetUp]
    public void SetUp()
    {
        _runReader = Substitute.For<IAgentRunReader>();
        _agentService = Substitute.For<IAIAgentService>();
        _logger = Substitute.For<ILogger<AgentRunReadController>>();
        _agentId = Guid.NewGuid();

        // Story 4.8 — default agent-service substitute returns null so all
        // pre-Story-4.8 tests assert against the same `AgentDisplayName == null`
        // contract they were pinned at (AC3.e option (i) — minimum-mutation).
        // Tests that exercise the happy path / throw path / OCE path override
        // this default per-test.
        _agentService.GetAgentAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AIAgent?>(null));

        _controller = new AgentRunReadController(_runReader, _agentService, _logger);
    }

    [TearDown]
    public void TearDown()
    {
        _controller?.Dispose();
    }

    private static AgentRunRecord MakeRunRecord(
        Guid agentId,
        string? responseSnapshotJoined,
        string runId = DefaultRunId,
        string? promptSnapshotJoined = "[user] prompt") => new(
        RunId: runId,
        AgentId: agentId,
        AgentVersion: 1,
        StartedUtc: new DateTime(2026, 5, 19, 12, 0, 0, DateTimeKind.Utc),
        CompletedUtc: new DateTime(2026, 5, 19, 12, 0, 5, DateTimeKind.Utc),
        AggregateStatus: AgentRunStatus.Succeeded,
        Error: null,
        PromptSnapshotJoined: promptSnapshotJoined,
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

    [Test]
    public async Task GetAsync_RunIdWhitespaceOnly_Returns400ProblemDetails()
    {
        var result = await _controller.GetAsync("   ", CancellationToken.None);

        var objectResult = result as ObjectResult;
        Assert.That(objectResult, Is.Not.Null);
        Assert.That(objectResult!.StatusCode, Is.EqualTo(400));
        var problem = (ProblemDetails)objectResult.Value!;
        Assert.That(problem.Detail, Does.Contain("cannot be empty"));
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
        Assert.That(response!.Score, Is.EqualTo(8), "7.5 rounds to 8.");
    }

    [TestCase(0)]
    [TestCase(11)]
    [TestCase(-1)]
    public async Task GetAsync_ScoreOutsideBrandVoiceRange_ReturnsNullScore(int score)
    {
        var responseJson = $$"""[assistant] {"score":{{score}},"issues":[{"text":"x"}],"suggestions":["s"]}""";
        _runReader.GetRunsForThreadAsync(DefaultRunId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentRunRecord>>(
                new[] { MakeRunRecord(_agentId, responseJson) }));

        var result = await _controller.GetAsync(DefaultRunId, CancellationToken.None);

        var response = (result as OkObjectResult)!.Value as AgentRunDetailResponse;
        Assert.That(response, Is.Not.Null);
        Assert.That(response!.Score, Is.Null);
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
        // assistant. Parser must use the final line-boundary [assistant] JSON
        // payload to skip intermediate rounds.
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

    [TestCase("[assistant]    {\"score\":6,\"issues\":[{\"text\":\"spaced\"}],\"suggestions\":[\"s\"]}")]
    [TestCase("[assistant]\n{\"score\":6,\"issues\":[{\"text\":\"newline\"}],\"suggestions\":[\"s\"]}")]
    public async Task GetAsync_AssistantTranscriptAllowsWhitespaceBeforeJsonObject(string transcript)
    {
        _runReader.GetRunsForThreadAsync(DefaultRunId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentRunRecord>>(
                new[] { MakeRunRecord(_agentId, transcript) }));

        var result = await _controller.GetAsync(DefaultRunId, CancellationToken.None);

        var response = (result as OkObjectResult)!.Value as AgentRunDetailResponse;
        Assert.That(response, Is.Not.Null);
        Assert.That(response!.Score, Is.EqualTo(6));
        Assert.That(response.Issues, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task GetAsync_QuotedAssistantMarkerInsideJsonString_DoesNotRedirectParser()
    {
        const string transcript = """
            [assistant] {"score":7,"issues":[{"text":"The literal [assistant] { marker can appear in content","reason":"quote"}],"suggestions":["Keep parser on transcript boundary"]}
            """;
        _runReader.GetRunsForThreadAsync(DefaultRunId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentRunRecord>>(
                new[] { MakeRunRecord(_agentId, transcript) }));

        var result = await _controller.GetAsync(DefaultRunId, CancellationToken.None);

        var response = (result as OkObjectResult)!.Value as AgentRunDetailResponse;
        Assert.That(response, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(response!.Score, Is.EqualTo(7));
            Assert.That(response.Issues, Has.Count.EqualTo(1));
            Assert.That(response.Issues[0].Text, Does.Contain("[assistant] {"));
        });
    }

    // ─────────────────────────────────────────────────────────────────────
    // AC4 (Story 4.5 Q2a) — Memory-injection parse from PromptSnapshotJoined
    // ─────────────────────────────────────────────────────────────────────

    private const string MemoryAnchor = "[system] Lessons from past runs:\n";

    [Test]
    public async Task GetAsync_NoMemoryInjectionInPromptSnapshot_ReturnsMemoryUsedFalseAndEmptyCitedMemories()
    {
        // Run 1 baseline. Default MakeRunRecord PromptSnapshotJoined = "[user] prompt".
        _runReader.GetRunsForThreadAsync(DefaultRunId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentRunRecord>>(
                new[] { MakeRunRecord(_agentId, responseSnapshotJoined: null) }));

        var result = await _controller.GetAsync(DefaultRunId, CancellationToken.None);

        var response = (result as OkObjectResult)!.Value as AgentRunDetailResponse;
        Assert.That(response, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(response!.MemoryUsed, Is.False);
            Assert.That(response.CitedMemories, Is.Empty);
        });
    }

    [Test]
    public async Task GetAsync_SingleMemoryInjection_ReturnsMemoryUsedTrueAndOneCitedMemory()
    {
        var prompt = MemoryAnchor
            + "• Run 347c2071 👎: digest text here — \"editor comment verbatim\"\n"
            + "\n"
            + "[user] Audit this content...\n";
        _runReader.GetRunsForThreadAsync(DefaultRunId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentRunRecord>>(
                new[] { MakeRunRecord(_agentId, responseSnapshotJoined: null, promptSnapshotJoined: prompt) }));

        var result = await _controller.GetAsync(DefaultRunId, CancellationToken.None);

        var response = (result as OkObjectResult)!.Value as AgentRunDetailResponse;
        Assert.That(response, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(response!.MemoryUsed, Is.True);
            Assert.That(response.CitedMemories, Has.Count.EqualTo(1));
            Assert.That(response.CitedMemories[0].RunIdPrefix, Is.EqualTo("347c2071"));
            Assert.That(response.CitedMemories[0].Emoji, Is.EqualTo("👎"));
            Assert.That(response.CitedMemories[0].CommentSnippet, Is.EqualTo("editor comment verbatim"));
        });
    }

    [Test]
    public async Task GetAsync_MultipleMemoryInjection_ReturnsMemoryUsedTrueAndAllCitedMemoriesInOrder()
    {
        var prompt = MemoryAnchor
            + "• Run aaaaaaaa 👎: summary 1 — \"comment 1\"\n"
            + "• Run bbbbbbbb 👍: summary 2 — \"comment 2\"\n"
            + "• Run cccccccc •: summary 3\n"
            + "• Run dddddddd 👎: summary 4 — \"comment 4\"\n"
            + "• Run eeeeeeee 👍: summary 5 — \"comment 5\"\n"
            + "\n[user] go\n";
        _runReader.GetRunsForThreadAsync(DefaultRunId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentRunRecord>>(
                new[] { MakeRunRecord(_agentId, responseSnapshotJoined: null, promptSnapshotJoined: prompt) }));

        var result = await _controller.GetAsync(DefaultRunId, CancellationToken.None);

        var response = (result as OkObjectResult)!.Value as AgentRunDetailResponse;
        Assert.That(response, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(response!.MemoryUsed, Is.True);
            Assert.That(response.CitedMemories, Has.Count.EqualTo(5));
            Assert.That(response.CitedMemories[0].RunIdPrefix, Is.EqualTo("aaaaaaaa"));
            Assert.That(response.CitedMemories[4].RunIdPrefix, Is.EqualTo("eeeeeeee"));
            Assert.That(response.CitedMemories[2].CommentSnippet, Is.Null,
                "Neutral bullet with no comment suffix — CommentSnippet = null per BuildMemorySystemMessage comment-null branch.");
        });
    }

    [Test]
    public async Task GetAsync_MemoryInjectionWithCommentExceeding300Chars_TruncatesCommentSnippetWithEllipsis()
    {
        var longComment = new string('x', 500);
        var prompt = MemoryAnchor
            + $"• Run 347c2071 👎: summary — \"{longComment}\"\n\n[user] go\n";
        _runReader.GetRunsForThreadAsync(DefaultRunId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentRunRecord>>(
                new[] { MakeRunRecord(_agentId, responseSnapshotJoined: null, promptSnapshotJoined: prompt) }));

        var result = await _controller.GetAsync(DefaultRunId, CancellationToken.None);

        var response = (result as OkObjectResult)!.Value as AgentRunDetailResponse;
        Assert.That(response, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(response!.CitedMemories, Has.Count.EqualTo(1));
            Assert.That(response.CitedMemories[0].CommentSnippet, Is.Not.Null);
            Assert.That(response.CitedMemories[0].CommentSnippet!.Length, Is.EqualTo(301),
                "300 chars + 1-char ellipsis (…) = 301.");
            Assert.That(response.CitedMemories[0].CommentSnippet!.EndsWith("…"), Is.True);
        });
    }

    [Test]
    public async Task GetAsync_MemoryInjectionExceedsCap_TrimsToMaxCitedMemories()
    {
        // 11 bullets. MaxCitedMemories = 10. AC4.e mandates that the cap test
        // ALSO proves the structured-output parse still populates Score /
        // Issues / Suggestions correctly — i.e. the cap doesn't bleed into the
        // rest of the response shape.
        var bullets = string.Join("\n", Enumerable.Range(0, 11)
            .Select(i => $"• Run {i:D8} 👎: summary {i} — \"comment {i}\""));
        var prompt = MemoryAnchor + bullets + "\n\n[user] go\n";
        const string responseJson = """
            {
              "score": 8,
              "issues": [
                { "text": "still flagged", "reason": "Guideline #6 colloquialism" }
              ],
              "suggestions": [
                "Rephrase 'still flagged' to direct nature description."
              ]
            }
            """;
        _runReader.GetRunsForThreadAsync(DefaultRunId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentRunRecord>>(
                new[] { MakeRunRecord(_agentId, responseSnapshotJoined: responseJson, promptSnapshotJoined: prompt) }));

        var result = await _controller.GetAsync(DefaultRunId, CancellationToken.None);

        var response = (result as OkObjectResult)!.Value as AgentRunDetailResponse;
        Assert.That(response, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(response!.MemoryUsed, Is.True);
            Assert.That(response.CitedMemories, Has.Count.EqualTo(AgentRunReadController.MaxCitedMemories),
                "Cap pinned by MaxCitedMemories = 10.");
            // AC4.e co-assertion — structured-output fields populate correctly
            // alongside the capped cited-memories list.
            Assert.That(response.Score, Is.EqualTo(8));
            Assert.That(response.Issues, Has.Count.EqualTo(1));
            Assert.That(response.Issues[0].Text, Is.EqualTo("still flagged"));
            Assert.That(response.Suggestions, Has.Count.EqualTo(1));
            Assert.That(response.Suggestions[0],
                Does.StartWith("Rephrase 'still flagged'"));
        });
    }

    [Test]
    public async Task GetAsync_MalformedMemoryInjectionBlock_ReturnsMemoryUsedFalseAndEmptyCitedMemoriesAndLogsWarning()
    {
        // Anchor matches but the subsequent lines don't follow "• Run {first8} {emoji}:" shape.
        var prompt = MemoryAnchor + "garbled content without bullet format\n[user] go\n";
        _runReader.GetRunsForThreadAsync(DefaultRunId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentRunRecord>>(
                new[] { MakeRunRecord(_agentId, responseSnapshotJoined: null, promptSnapshotJoined: prompt) }));

        var result = await _controller.GetAsync(DefaultRunId, CancellationToken.None);

        var response = (result as OkObjectResult)!.Value as AgentRunDetailResponse;
        Assert.That(response, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(response!.MemoryUsed, Is.False);
            Assert.That(response.CitedMemories, Is.Empty);
        });
        _logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Test]
    public async Task GetAsync_MemoryInjectionBulletWithNoCommentSuffix_ReturnsCitedMemoryWithNullCommentSnippet()
    {
        // BuildMemorySystemMessage's comment-null branch (line 228-230).
        var prompt = MemoryAnchor
            + "• Run 347c2071 👍: digest-only summary\n"
            + "\n[user] go\n";
        _runReader.GetRunsForThreadAsync(DefaultRunId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentRunRecord>>(
                new[] { MakeRunRecord(_agentId, responseSnapshotJoined: null, promptSnapshotJoined: prompt) }));

        var result = await _controller.GetAsync(DefaultRunId, CancellationToken.None);

        var response = (result as OkObjectResult)!.Value as AgentRunDetailResponse;
        Assert.That(response, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(response!.MemoryUsed, Is.True);
            Assert.That(response.CitedMemories, Has.Count.EqualTo(1));
            Assert.That(response.CitedMemories[0].CommentSnippet, Is.Null);
        });
    }

    // ─────────────────────────────────────────────────────────────────────
    // Story 4.8 AC3 — AgentDisplayName resolution via IAIAgentService
    // happy + null + throw + OCE
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public async Task GetAsync_AgentExists_PopulatesAgentDisplayName()
    {
        // AC3.a — happy path. Stubbed IAIAgentService returns the canonical
        // demo agent shape (Name = "Brand Voice Auditor" per
        // templates/brand-audit/agent.json).
        _runReader.GetRunsForThreadAsync(DefaultRunId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentRunRecord>>(
                new[] { MakeRunRecord(_agentId, responseSnapshotJoined: null) }));
        _agentService.GetAgentAsync(_agentId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AIAgent?>(new AIAgent
            {
                Alias = "brand-voice-auditor",
                Name = "Brand Voice Auditor",
            }));

        var result = await _controller.GetAsync(DefaultRunId, CancellationToken.None);

        var response = (result as OkObjectResult)!.Value as AgentRunDetailResponse;
        Assert.That(response, Is.Not.Null);
        Assert.That(response!.AgentDisplayName, Is.EqualTo("Brand Voice Auditor"));
    }

    [Test]
    public async Task GetAsync_AgentDeleted_FallsBackToNullDisplayName()
    {
        // AC3.b — override path. IAIAgentService returns null (agent deleted
        // between run-time audit-logging and read-time modal opening — a
        // legitimate v0.2 multi-week-adopter signal, not an error). 200 OK +
        // other fields populated; widget falls back to "Agent {agentId}".
        _runReader.GetRunsForThreadAsync(DefaultRunId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentRunRecord>>(
                new[] { MakeRunRecord(_agentId, responseSnapshotJoined: null) }));
        _agentService.GetAgentAsync(_agentId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AIAgent?>(null));

        var result = await _controller.GetAsync(DefaultRunId, CancellationToken.None);

        var ok = result as OkObjectResult;
        Assert.That(ok, Is.Not.Null);
        var response = ok!.Value as AgentRunDetailResponse;
        Assert.That(response, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(response!.AgentDisplayName, Is.Null);
            Assert.That(response.RunId, Is.EqualTo(DefaultRunId));
            Assert.That(response.AgentId, Is.EqualTo(_agentId));
        });
        // No Warning log emitted on the legitimate-null branch — null is a
        // valid signal, not an error condition.
        _logger.DidNotReceive().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Test]
    public async Task GetAsync_AgentServiceThrows_FallsBackToNullDisplayName_LogsWarning()
    {
        // AC3.c — edge: transient throw from IAIAgentService. NFR-R1 graceful
        // degradation per Story 4.8 § Locked decision 3 (mirror of
        // AgentFeedbackReadController.ResolveDisplayNamesAsync). 200 OK +
        // AgentDisplayName = null + Warning log emitted. Log-state assertion
        // pins the AgentId stringification so a future refactor that drops
        // the {AgentId} placeholder from the LogWarning call would fail this
        // test rather than silently degrade ops diagnostics.
        _runReader.GetRunsForThreadAsync(DefaultRunId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentRunRecord>>(
                new[] { MakeRunRecord(_agentId, responseSnapshotJoined: null) }));
        _agentService.GetAgentAsync(_agentId, Arg.Any<CancellationToken>())
            .Returns<Task<AIAgent?>>(_ => throw new InvalidOperationException("simulated transient"));

        var result = await _controller.GetAsync(DefaultRunId, CancellationToken.None);

        var ok = result as OkObjectResult;
        Assert.That(ok, Is.Not.Null,
            "Throws from IAIAgentService degrade gracefully — endpoint still returns 200 OK.");
        var response = ok!.Value as AgentRunDetailResponse;
        Assert.That(response, Is.Not.Null);
        Assert.That(response!.AgentDisplayName, Is.Null);
        _logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(state => state != null && state.ToString()!.Contains(_agentId.ToString())),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Test]
    public void GetAsync_AgentServiceCancelled_RethrowsOperationCanceledException()
    {
        // AC3.d — edge: cancellation. OperationCanceledException MUST NOT be
        // swallowed by the catch-Exception arm of ResolveAgentDisplayNameAsync
        // (verbatim shape mirror of AgentFeedbackReadController.ResolveDisplayNamesAsync
        // OCE-rethrow contract). The CT-rethrow contract is genuinely
        // load-bearing here — unlike IUserService.GetAsync(IEnumerable<Guid>)
        // which has no CT overload in 17.3.2 (deferred-work.md line 417),
        // IAIAgentService.GetAgentAsync DOES accept a CT param (line 22 of the
        // upstream interface).
        //
        // Review-patch: stub the substitute to return Task.FromCanceled<T>
        // rather than throwing synchronously from the substitute factory.
        // The synchronous-throw form would pass even if the controller
        // stripped its ConfigureAwait or wrapped OCE in AggregateException;
        // the cancelled-task form exercises the actual awaited-throw path.
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        _runReader.GetRunsForThreadAsync(DefaultRunId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentRunRecord>>(
                new[] { MakeRunRecord(_agentId, responseSnapshotJoined: null) }));
        _agentService.GetAgentAsync(_agentId, Arg.Any<CancellationToken>())
            .Returns(Task.FromCanceled<AIAgent?>(cts.Token));

        // CatchAsync (not ThrowsAsync) — Task.FromCanceled<T> resolves to
        // TaskCanceledException, which derives from OperationCanceledException.
        // ThrowsAsync requires exact type match; CatchAsync accepts derived
        // types, which is what "the catch arm does not swallow OCE" actually
        // means in production (the catch arm is `catch (OperationCanceledException)`,
        // matching both the base and its TaskCanceledException subtype).
        Assert.CatchAsync<OperationCanceledException>(
            async () => await _controller.GetAsync(DefaultRunId, cts.Token));
    }

    [Test]
    public async Task GetAsync_AgentIdIsGuidEmpty_ShortCircuitsToNullDisplayNameWithoutCallingAgentService()
    {
        // Review-patch (decision #2): defensive short-circuit for unresolved
        // AgentId. If the audit-log middleware ever stamps Guid.Empty (an
        // upstream defect that has been observed historically), the helper
        // must skip the upstream call entirely rather than emit a Warning per
        // modal open. Pins both the null response AND the absence of the
        // upstream invocation.
        _runReader.GetRunsForThreadAsync(DefaultRunId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentRunRecord>>(
                new[] { MakeRunRecord(Guid.Empty, responseSnapshotJoined: null) }));

        var result = await _controller.GetAsync(DefaultRunId, CancellationToken.None);

        var response = (result as OkObjectResult)!.Value as AgentRunDetailResponse;
        Assert.That(response, Is.Not.Null);
        Assert.That(response!.AgentDisplayName, Is.Null);
        await _agentService.DidNotReceive().GetAgentAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        _logger.DidNotReceive().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Test]
    public async Task GetAsync_AgentNameIsWhitespaceOnly_NormalisesToNullDisplayName()
    {
        // Review-patch (decision #1): server-side normalisation. A misconfigured
        // or imported agent with an empty/whitespace-only Name would otherwise
        // surface as AgentDisplayName == "" — the widget's nullish-coalesce
        // operator only catches null/undefined, so an empty string would render
        // a blank attribution line rather than the "Agent {first-8}" fallback.
        // Server trims + coerces empty → null to keep the API contract clean.
        _runReader.GetRunsForThreadAsync(DefaultRunId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentRunRecord>>(
                new[] { MakeRunRecord(_agentId, responseSnapshotJoined: null) }));
        _agentService.GetAgentAsync(_agentId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AIAgent?>(new AIAgent
            {
                Alias = "misconfigured",
                Name = "   ",
            }));

        var result = await _controller.GetAsync(DefaultRunId, CancellationToken.None);

        var response = (result as OkObjectResult)!.Value as AgentRunDetailResponse;
        Assert.That(response, Is.Not.Null);
        Assert.That(response!.AgentDisplayName, Is.Null,
            "Whitespace-only Name normalises to null so the widget falls through to the GUID-prefix display.");
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

    // ═════════════════════════════════════════════════════════════════════
    // Story 4.12 — sibling endpoint + selectedRunId query (AC5 backend tests)
    // ═════════════════════════════════════════════════════════════════════

    private const string ThreadId = "thread-batch-1";

    private static AgentRunRecord MakeSibling(
        Guid agentId,
        string runId,
        DateTime startedUtc,
        string threadId = ThreadId) => new(
        RunId: runId,
        AgentId: agentId,
        AgentVersion: 1,
        StartedUtc: startedUtc,
        CompletedUtc: startedUtc.AddSeconds(5),
        AggregateStatus: AgentRunStatus.Succeeded,
        Error: null,
        PromptSnapshotJoined: $"[user] iteration {runId}",
        ResponseSnapshotJoined: $"[assistant] {{\"score\":7,\"issues\":[{{\"text\":\"flag-{runId}\"}}],\"suggestions\":[\"sugg-{runId}\"]}}",
        TokenCountInput: 100,
        TokenCountOutput: 20,
        ThreadId: threadId,
        UserId: "user-1",
        TraceId: $"trace-{runId}");

    [Test]
    public async Task GetSiblingsAsync_KnownThreadId_SurfacesAllSiblings_AscByStartedUtc()
    {
        // Reader returns 6 records DESC (Story 1.2 contract). Endpoint sorts ASC
        // so the picker walks oldest → newest (Story 4.12 LD#3a).
        var baseTime = new DateTime(2026, 5, 21, 17, 0, 0, DateTimeKind.Utc);
        var records = new[]
        {
            MakeSibling(_agentId, "rid-6", baseTime.AddSeconds(50)),
            MakeSibling(_agentId, "rid-5", baseTime.AddSeconds(40)),
            MakeSibling(_agentId, "rid-4", baseTime.AddSeconds(30)),
            MakeSibling(_agentId, "rid-3", baseTime.AddSeconds(20)),
            MakeSibling(_agentId, "rid-2", baseTime.AddSeconds(10)),
            MakeSibling(_agentId, "rid-1", baseTime),
        };
        _runReader.GetRunsForThreadAsync(ThreadId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentRunRecord>>(records));

        var result = await _controller.GetSiblingsAsync(ThreadId, CancellationToken.None);

        var ok = result as OkObjectResult;
        Assert.That(ok, Is.Not.Null, "Happy path returns Ok(list).");
        var siblings = ok!.Value as IReadOnlyList<AgentRunSiblingResponse>;
        Assert.That(siblings, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(siblings, Has.Count.EqualTo(6));
            Assert.That(siblings![0].RunId, Is.EqualTo("rid-1"), "ASC sort — oldest iteration first.");
            Assert.That(siblings[5].RunId, Is.EqualTo("rid-6"), "ASC sort — newest iteration last.");
            Assert.That(siblings.All(s => s.ThreadId == ThreadId), Is.True,
                "Every sibling carries the supplied ThreadId.");
        });
    }

    [Test]
    public async Task GetSiblingsAsync_UnknownThreadId_Returns200WithEmptyList()
    {
        _runReader.GetRunsForThreadAsync("missing-thread", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentRunRecord>>(Array.Empty<AgentRunRecord>()));

        var result = await _controller.GetSiblingsAsync("missing-thread", CancellationToken.None);

        var ok = result as OkObjectResult;
        Assert.That(ok, Is.Not.Null,
            "Unknown thread returns 200 + empty list (not 404) so the modal still renders.");
        var siblings = ok!.Value as IReadOnlyList<AgentRunSiblingResponse>;
        Assert.That(siblings, Is.Not.Null);
        Assert.That(siblings, Is.Empty);
    }

    [Test]
    public async Task GetSiblingsAsync_SingleIterationThread_ReturnsOneSibling()
    {
        var record = MakeSibling(_agentId, "rid-solo", DateTime.UtcNow);
        _runReader.GetRunsForThreadAsync(ThreadId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentRunRecord>>(new[] { record }));

        var result = await _controller.GetSiblingsAsync(ThreadId, CancellationToken.None);

        var siblings = (result as OkObjectResult)!.Value as IReadOnlyList<AgentRunSiblingResponse>;
        Assert.That(siblings, Has.Count.EqualTo(1));
        Assert.That(siblings![0].RunId, Is.EqualTo("rid-solo"));
    }

    [Test]
    public async Task GetSiblingsAsync_WhitespaceThreadId_Returns400_NoReaderCall()
    {
        var result = await _controller.GetSiblingsAsync("   ", CancellationToken.None);

        var objectResult = result as ObjectResult;
        Assert.That(objectResult, Is.Not.Null);
        Assert.That(objectResult!.StatusCode, Is.EqualTo(400));
        await _runReader.DidNotReceiveWithAnyArgs().GetRunsForThreadAsync(default!, default);
    }

    [Test]
    public async Task GetSiblingsAsync_ThreadIdExceeds256Chars_Returns400_NoReaderCall()
    {
        var oversized = new string('a', 257);

        var result = await _controller.GetSiblingsAsync(oversized, CancellationToken.None);

        var objectResult = result as ObjectResult;
        Assert.That(objectResult, Is.Not.Null);
        Assert.That(objectResult!.StatusCode, Is.EqualTo(400));
        var problem = (ProblemDetails)objectResult.Value!;
        Assert.That(problem.Detail, Does.Contain("256"));
        await _runReader.DidNotReceiveWithAnyArgs().GetRunsForThreadAsync(default!, default);
    }

    [Test]
    public async Task GetSiblingsAsync_ReaderThrowsNonCancellation_ReturnsEmptyAndLogsWarning()
    {
        _runReader.GetRunsForThreadAsync(ThreadId, Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<AgentRunRecord>>>(_ =>
                Task.FromException<IReadOnlyList<AgentRunRecord>>(new InvalidOperationException("audit-log offline")));

        var result = await _controller.GetSiblingsAsync(ThreadId, CancellationToken.None);

        var ok = result as OkObjectResult;
        Assert.That(ok, Is.Not.Null, "NFR-R3 graceful degradation — empty list on reader throw.");
        var siblings = ok!.Value as IReadOnlyList<AgentRunSiblingResponse>;
        Assert.That(siblings, Is.Empty);

        // P21 — pin log content so a future regression that swallows the
        // exception (or logs a misleading message) fails the test.
        _logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("GetSiblingsAsync")
                && o.ToString()!.Contains("GetRunsForThreadAsync threw")),
            Arg.Is<Exception>(e => e is InvalidOperationException
                && e.Message == "audit-log offline"),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Test]
    public void GetSiblingsAsync_OperationCanceledException_PropagatesUnwrapped()
    {
        // OperationCanceledException is the canonical "client navigated away"
        // signal — controllers must rethrow, never swallow.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _runReader.GetRunsForThreadAsync(ThreadId, Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<AgentRunRecord>>>(_ =>
                Task.FromException<IReadOnlyList<AgentRunRecord>>(new OperationCanceledException()));

        Assert.ThrowsAsync<OperationCanceledException>(
            () => _controller.GetSiblingsAsync(ThreadId, cts.Token));
    }

    [Test]
    public async Task GetAsync_SelectedRunId_SelectsMatchingSibling_NotRunsZero()
    {
        // Picker submits selectedRunId pointing at iteration #2 in a 3-iteration
        // batch. Endpoint must surface that iteration's prompt/response, not the
        // most-recent (runs[0]) one. SelectedRunId echoes back in the response.
        var baseTime = new DateTime(2026, 5, 21, 17, 0, 0, DateTimeKind.Utc);
        var records = new[]
        {
            MakeSibling(_agentId, "rid-step-3", baseTime.AddSeconds(20)), // runs[0] — most recent
            MakeSibling(_agentId, "rid-step-2", baseTime.AddSeconds(10)),
            MakeSibling(_agentId, "rid-step-1", baseTime),
        };
        _runReader.GetRunsForThreadAsync(ThreadId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentRunRecord>>(records));

        var result = await _controller.GetAsync(ThreadId, CancellationToken.None, selectedRunId: "rid-step-2");

        var ok = result as OkObjectResult;
        Assert.That(ok, Is.Not.Null);
        var response = ok!.Value as AgentRunDetailResponse;
        Assert.That(response, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(response!.RunId, Is.EqualTo(ThreadId),
                "Response RunId remains the ThreadId for legacy compat.");
            Assert.That(response.SelectedRunId, Is.EqualTo("rid-step-2"),
                "Picker selectedRunId echoes back so the widget can verify which iteration rendered.");
            Assert.That(response.Issues, Has.Count.EqualTo(1));
            Assert.That(response.Issues[0].Text, Is.EqualTo("flag-rid-step-2"),
                "Surfaced output is the SELECTED iteration's response — NOT runs[0].");
        });
    }

    [Test]
    public async Task GetAsync_SelectedRunIdUnknownForThread_Returns404ProblemDetails()
    {
        var records = new[]
        {
            MakeSibling(_agentId, "rid-step-1", DateTime.UtcNow),
        };
        _runReader.GetRunsForThreadAsync(ThreadId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentRunRecord>>(records));

        var result = await _controller.GetAsync(ThreadId, CancellationToken.None, selectedRunId: "rid-not-here");

        var objectResult = result as ObjectResult;
        Assert.That(objectResult, Is.Not.Null);
        Assert.That(objectResult!.StatusCode, Is.EqualTo(404));
        var problem = (ProblemDetails)objectResult.Value!;
        Assert.That(problem.Title, Is.EqualTo("Selected iteration not found."));
    }

    [Test]
    public async Task GetAsync_SelectedRunIdOmitted_PreservesPreStory412Behaviour_PicksRunsZero()
    {
        // Byte-compatibility pin for Story 4.5 — when no selectedRunId is
        // supplied (legacy widget or non-picker mode), the controller picks
        // runs[0] (most-recent) just like before Story 4.12.
        var baseTime = new DateTime(2026, 5, 21, 17, 0, 0, DateTimeKind.Utc);
        var records = new[]
        {
            MakeSibling(_agentId, "rid-newest", baseTime.AddSeconds(20)),
            MakeSibling(_agentId, "rid-older", baseTime),
        };
        _runReader.GetRunsForThreadAsync(ThreadId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentRunRecord>>(records));

        var result = await _controller.GetAsync(ThreadId, CancellationToken.None);

        var response = (result as OkObjectResult)!.Value as AgentRunDetailResponse;
        Assert.That(response, Is.Not.Null);
        Assert.That(response!.SelectedRunId, Is.Null,
            "Legacy non-picker callers see SelectedRunId = null in the response.");
        Assert.That(response.Issues[0].Text, Is.EqualTo("flag-rid-newest"),
            "Legacy behaviour selects runs[0] from DESC-ordered list.");
    }

    [TestCase("   ")]
    [TestCase("\t")]
    [TestCase("")]
    public async Task GetAsync_SelectedRunIdWhitespaceOnly_Returns400_NoReaderCall(string whitespaceValue)
    {
        // P11 — symmetric with POST controller's whitespace guard. Silently
        // treating whitespace-only as "legacy mode" would mask client contract
        // drift.
        var result = await _controller.GetAsync(ThreadId, CancellationToken.None, selectedRunId: whitespaceValue);

        var objectResult = result as ObjectResult;
        Assert.That(objectResult, Is.Not.Null);
        Assert.That(objectResult!.StatusCode, Is.EqualTo(400));
        var problem = (ProblemDetails)objectResult.Value!;
        Assert.That(problem.Detail, Does.Contain("selectedRunId"));
        await _runReader.DidNotReceiveWithAnyArgs().GetRunsForThreadAsync(default!, default);
    }

    [Test]
    public async Task GetAsync_SelectedRunIdMatchesMultipleSiblings_PicksFirst_LogsWarning()
    {
        // P3 — single-row-per-RunId contract is documented but not enforced
        // by the audit-log writer; FirstOrDefault + warn-log fallback handles
        // any future tool-call follow-up retry that re-uses a RunId without
        // throwing 500.
        var baseTime = new DateTime(2026, 5, 21, 17, 0, 0, DateTimeKind.Utc);
        var records = new[]
        {
            MakeSibling(_agentId, "rid-dup", baseTime.AddSeconds(20)), // first match
            MakeSibling(_agentId, "rid-dup", baseTime.AddSeconds(10)), // duplicate
        };
        _runReader.GetRunsForThreadAsync(ThreadId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentRunRecord>>(records));

        var result = await _controller.GetAsync(ThreadId, CancellationToken.None, selectedRunId: "rid-dup");

        var ok = result as OkObjectResult;
        Assert.That(ok, Is.Not.Null, "Duplicate-match resolves to first sibling, not 500.");

        _logger.Received().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("single-row-per-RunId contract violated")),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Test]
    public async Task GetSiblingsAsync_ResponseThreadIdSourcedFromRow_NotRouteParameter()
    {
        // P5 — controller projects from `r.ThreadId` (persisted row value)
        // rather than echoing the route parameter. Any future reader-layer
        // filter bug surfaces as a visible mismatch instead of being masked
        // by client-input echo.
        const string queryThreadId = "thread-from-query";
        var record = MakeSibling(_agentId, "rid-only", DateTime.UtcNow, threadId: queryThreadId);
        _runReader.GetRunsForThreadAsync(queryThreadId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentRunRecord>>(new[] { record }));

        var result = await _controller.GetSiblingsAsync(queryThreadId, CancellationToken.None);

        var siblings = (result as OkObjectResult)!.Value as IReadOnlyList<AgentRunSiblingResponse>;
        Assert.That(siblings, Has.Count.EqualTo(1));
        Assert.That(siblings![0].ThreadId, Is.EqualTo(queryThreadId),
            "Happy path: row ThreadId equals query ThreadId, so the response carries that value.");
    }

    [Test]
    public async Task GetSiblingsAsync_OrderingWithEqualStartedUtc_TiebreaksOnRunIdOrdinal()
    {
        // P9 — deterministic order on equal StartedUtc (parallel-fork
        // iterations sharing microsecond-identical timestamps). Without the
        // RunId tiebreaker, picker indexes drift between page loads.
        var sameTime = new DateTime(2026, 5, 21, 17, 0, 0, DateTimeKind.Utc);
        var records = new[]
        {
            MakeSibling(_agentId, "rid-c", sameTime),
            MakeSibling(_agentId, "rid-a", sameTime),
            MakeSibling(_agentId, "rid-b", sameTime),
        };
        _runReader.GetRunsForThreadAsync(ThreadId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentRunRecord>>(records));

        var result = await _controller.GetSiblingsAsync(ThreadId, CancellationToken.None);

        var siblings = (result as OkObjectResult)!.Value as IReadOnlyList<AgentRunSiblingResponse>;
        Assert.That(siblings, Has.Count.EqualTo(3));
        Assert.Multiple(() =>
        {
            Assert.That(siblings![0].RunId, Is.EqualTo("rid-a"), "RunId ordinal tiebreak — alphabetical first.");
            Assert.That(siblings[1].RunId, Is.EqualTo("rid-b"));
            Assert.That(siblings[2].RunId, Is.EqualTo("rid-c"));
        });
    }

    [Test]
    public async Task GetSiblingsAsync_RowWithNullThreadId_DroppedAndWarned()
    {
        // P5 (defensive companion) — rows with null ThreadId indicate a
        // reader-layer filter bug; they must not be projected with a
        // fabricated ThreadId. Filtered + warn-logged.
        var record = MakeSibling(_agentId, "rid-only", DateTime.UtcNow);
        var orphan = record with { ThreadId = null };
        _runReader.GetRunsForThreadAsync(ThreadId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentRunRecord>>(new[] { orphan }));

        var result = await _controller.GetSiblingsAsync(ThreadId, CancellationToken.None);

        var siblings = (result as OkObjectResult)!.Value as IReadOnlyList<AgentRunSiblingResponse>;
        Assert.That(siblings, Is.Empty, "Rows with null ThreadId are dropped, not projected.");

        _logger.Received().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("null ThreadId")),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }
}
