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
