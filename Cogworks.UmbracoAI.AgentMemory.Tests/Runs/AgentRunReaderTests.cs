using Cogworks.UmbracoAI.AgentMemory.Configuration;
using Cogworks.UmbracoAI.AgentMemory.Runs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.AI.Core.AuditLog;
using Umbraco.AI.Core.Models;

namespace Cogworks.UmbracoAI.AgentMemory.Tests.Runs;

/// <summary>
/// Tests for <see cref="AgentRunReader"/>. Mocks <see cref="IAIAuditLogService"/> via
/// NSubstitute; constructs real <see cref="AIAuditLog"/> instances via the
/// object-initializer surface (verified at Tasks 5/7 AR26 pre-flight — the type is a
/// <c>public sealed class</c> with <c>init</c> accessors on every field we need).
/// </summary>
[TestFixture]
public class AgentRunReaderTests
{
    private const string RunIdKey = "Umbraco.AI.Agent.RunId";
    private const string ThreadIdKey = "Umbraco.AI.Agent.ThreadId";

    private static readonly Guid SampleAgentId = Guid.Parse("9bd5a738-08b2-4120-a65a-d5f580629290");
    private static readonly DateTime BaseUtc = new(2026, 5, 11, 8, 0, 0, DateTimeKind.Utc);

    private static AgentRunReader CreateSut(IAIAuditLogService auditLogService, int maxMemoryAgeDays = 90)
    {
        var options = Options.Create(new AgentMemoryOptions { MaxMemoryAgeDays = maxMemoryAgeDays });
        var logger = Substitute.For<ILogger<AgentRunReader>>();
        return new AgentRunReader(auditLogService, options, logger);
    }

    private static (AgentRunReader sut, IAIAuditLogService auditLogService, ILogger<AgentRunReader> logger)
        CreateSutWithLogger(int maxMemoryAgeDays = 90)
    {
        var auditLogService = Substitute.For<IAIAuditLogService>();
        var options = Options.Create(new AgentMemoryOptions { MaxMemoryAgeDays = maxMemoryAgeDays });
        var logger = Substitute.For<ILogger<AgentRunReader>>();
        var sut = new AgentRunReader(auditLogService, options, logger);
        return (sut, auditLogService, logger);
    }

    private static AIAuditLog Row(
        Guid? agentId = null,
        string? runIdValue = "rid-1",
        string? threadIdValue = "tid-1",
        AIAuditLogStatus status = AIAuditLogStatus.Succeeded,
        DateTime? startTime = null,
        DateTime? endTime = null,
        int? inputTokens = 100,
        int? outputTokens = 20,
        string? userId = "user-1",
        string? traceId = "trace-1",
        int? featureVersion = 2,
        string? prompt = "[user] hello",
        string? response = "[assistant] hi",
        string? errorMessage = null,
        IReadOnlyDictionary<string, string>? metadataOverride = null,
        bool metadataNull = false,
        bool metadataEmpty = false)
    {
        IReadOnlyDictionary<string, string>? metadata;
        if (metadataNull)
        {
            metadata = null;
        }
        else if (metadataEmpty)
        {
            metadata = new Dictionary<string, string>();
        }
        else if (metadataOverride is not null)
        {
            metadata = metadataOverride;
        }
        else
        {
            var dict = new Dictionary<string, string>();
            if (runIdValue is not null) dict[RunIdKey] = runIdValue;
            if (threadIdValue is not null) dict[ThreadIdKey] = threadIdValue;
            metadata = dict;
        }

        return new AIAuditLog
        {
            StartTime = startTime ?? BaseUtc,
            EndTime = endTime ?? (startTime ?? BaseUtc).AddSeconds(5),
            Status = status,
            UserId = userId,
            TraceId = traceId,
            Capability = AICapability.Chat,
            FeatureType = "agent",
            FeatureId = agentId ?? SampleAgentId,
            FeatureVersion = featureVersion,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            PromptSnapshot = prompt,
            ResponseSnapshot = response,
            ErrorMessage = errorMessage,
            Metadata = metadata,
        };
    }

    private static void ReturnsRows(IAIAuditLogService svc, IEnumerable<AIAuditLog> rows)
    {
        svc.GetAuditLogsPagedAsync(
                Arg.Any<AIAuditLogFilter>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(((IEnumerable<AIAuditLog>)rows.ToList(), rows.Count()));
    }

    // ---------- GetRecentRunsForAgentAsync ----------

    [Test]
    public async Task GetRecentRunsForAgentAsync_HappyPath_ReturnsProjectedRecords_OrderedByStartedUtcDescending()
    {
        var (sut, svc, _) = CreateSutWithLogger();
        var rows = new[]
        {
            Row(runIdValue: "rid-a", threadIdValue: "tid-1", startTime: BaseUtc.AddMinutes(0), inputTokens: 100, outputTokens: 20),
            Row(runIdValue: "rid-b", threadIdValue: "tid-1", startTime: BaseUtc.AddMinutes(10), inputTokens: 150, outputTokens: 25),
            Row(runIdValue: "rid-c", threadIdValue: "tid-2", startTime: BaseUtc.AddMinutes(20), inputTokens: 200, outputTokens: 30),
        };
        ReturnsRows(svc, rows);

        var result = await sut.GetRecentRunsForAgentAsync(SampleAgentId, take: 10, CancellationToken.None);

        Assert.That(result, Has.Count.EqualTo(3));
        Assert.That(result.Select(r => r.RunId).ToArray(), Is.EqualTo(new[] { "rid-c", "rid-b", "rid-a" }),
            "records must be ordered by StartedUtc descending");
        var first = result[0];
        Assert.Multiple(() =>
        {
            Assert.That(first.AgentId, Is.EqualTo(SampleAgentId));
            Assert.That(first.AgentVersion, Is.EqualTo(2));
            Assert.That(first.AggregateStatus, Is.EqualTo(AgentRunStatus.Succeeded));
            Assert.That(first.ThreadId, Is.EqualTo("tid-2"));
            Assert.That(first.UserId, Is.EqualTo("user-1"));
            Assert.That(first.TraceId, Is.EqualTo("trace-1"));
            Assert.That(first.TokenCountInput, Is.EqualTo(200));
            Assert.That(first.TokenCountOutput, Is.EqualTo(30));
            Assert.That(first.PromptSnapshotJoined, Is.EqualTo("[user] hello"));
            Assert.That(first.ResponseSnapshotJoined, Is.EqualTo("[assistant] hi"));
            Assert.That(first.Error, Is.Null);
        });
    }

    [TestCase(0, 0, Description = "take = 0 → empty list")]
    [TestCase(-1, 0, Description = "take = -1 → empty list")]
    [TestCase(-100, 0, Description = "take = -100 → empty list")]
    [TestCase(500, 100, Description = "take > 100 → clamped to 100")]
    public async Task GetRecentRunsForAgentAsync_TakeClamping(int requestedTake, int expectedReturnedCount)
    {
        var (sut, svc, _) = CreateSutWithLogger();

        // Generate 150 RunId-bearing rows to exercise the upper clamp.
        var rows = Enumerable.Range(0, 150)
            .Select(i => Row(
                runIdValue: $"rid-{i:D3}",
                threadIdValue: "tid-1",
                startTime: BaseUtc.AddSeconds(i)))
            .ToArray();
        ReturnsRows(svc, rows);

        var result = await sut.GetRecentRunsForAgentAsync(SampleAgentId, take: requestedTake, CancellationToken.None);

        Assert.That(result, Has.Count.EqualTo(expectedReturnedCount));
    }

    [Test]
    public async Task GetRecentRunsForAgentAsync_FiltersNullAndEmptyMetadata_ProjectsThreadIdNullWhenMissing()
    {
        var (sut, svc, _) = CreateSutWithLogger();
        var rows = new[]
        {
            // 1) Both RunId + ThreadId
            Row(runIdValue: "rid-a", threadIdValue: "tid-1", startTime: BaseUtc.AddMinutes(0)),
            // 2) RunId only — missing ThreadId
            Row(runIdValue: "rid-b", threadIdValue: null, startTime: BaseUtc.AddMinutes(5)),
            // 3) Metadata = null (Branch 2 — Automate path / programmatic) — filtered out
            Row(metadataNull: true, startTime: BaseUtc.AddMinutes(10)),
            // 4) Metadata = empty dictionary — filtered out
            Row(metadataEmpty: true, startTime: BaseUtc.AddMinutes(15)),
        };
        ReturnsRows(svc, rows);

        var result = await sut.GetRecentRunsForAgentAsync(SampleAgentId, take: 10, CancellationToken.None);

        Assert.That(result, Has.Count.EqualTo(2),
            "null and empty Metadata rows must be filtered out of the GROUP BY");
        var byRunId = result.ToDictionary(r => r.RunId);
        Assert.That(byRunId["rid-a"].ThreadId, Is.EqualTo("tid-1"));
        Assert.That(byRunId["rid-b"].ThreadId, Is.Null,
            "missing ThreadId metadata key projects as null ThreadId on the record");
    }

    [Test]
    public async Task GetRecentRunsForAgentAsync_ToolCallScenario_NRowsSameThreadId_ProduceNDistinctRecords()
    {
        // DRIFT-NEW-3 (0-c § Tool-call shape finding): a tool-using user message produces
        // N audit rows with N distinct RunIds sharing one ThreadId in v1.9.0. Each row →
        // its own AgentRunRecord; aggregation degenerates to single-row groups.
        var (sut, svc, _) = CreateSutWithLogger();
        var rows = new[]
        {
            Row(runIdValue: "rid-tool-1", threadIdValue: "tid-conv", startTime: BaseUtc.AddSeconds(0),
                inputTokens: 351, outputTokens: 11),
            Row(runIdValue: "rid-tool-2", threadIdValue: "tid-conv", startTime: BaseUtc.AddSeconds(2),
                inputTokens: 617, outputTokens: 35),
            Row(runIdValue: "rid-tool-3", threadIdValue: "tid-conv", startTime: BaseUtc.AddSeconds(4),
                inputTokens: 471, outputTokens: 33),
        };
        ReturnsRows(svc, rows);

        var result = await sut.GetRecentRunsForAgentAsync(SampleAgentId, take: 10, CancellationToken.None);

        Assert.That(result, Has.Count.EqualTo(3),
            "tool-call scenario: each chat row is its own AgentRunRecord, NOT aggregated");
        Assert.That(result.Select(r => r.ThreadId).Distinct().ToArray(), Is.EqualTo(new[] { "tid-conv" }),
            "all three records share the conversation-level ThreadId");
        Assert.That(result.Select(r => r.TokenCountInput).ToArray(), Is.EquivalentTo(new int?[] { 351, 617, 471 }),
            "MIN/MAX/SUM degenerate to single-row values per record (NOT summed across the conversation)");
    }

    // ---------- GetRunAsync ----------

    [Test]
    public async Task GetRunAsync_HappyPath_ReturnsProjectedRecord()
    {
        var (sut, svc, _) = CreateSutWithLogger();
        var rows = new[]
        {
            Row(runIdValue: "rid-target", threadIdValue: "tid-1", inputTokens: 250, outputTokens: 50),
            Row(runIdValue: "rid-other",  threadIdValue: "tid-1"),
        };
        ReturnsRows(svc, rows);

        var result = await sut.GetRunAsync("rid-target", CancellationToken.None);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.RunId, Is.EqualTo("rid-target"));
        Assert.That(result.TokenCountInput, Is.EqualTo(250));
        Assert.That(result.TokenCountOutput, Is.EqualTo(50));
        Assert.That(result.AgentId, Is.EqualTo(SampleAgentId));
        Assert.That(result.ThreadId, Is.EqualTo("tid-1"));
    }

    [Test]
    public async Task GetRunAsync_NotFoundInRecentWindow_ReturnsNull()
    {
        var (sut, svc, _) = CreateSutWithLogger();
        var rows = new[]
        {
            Row(runIdValue: "rid-other-1"),
            Row(runIdValue: "rid-other-2"),
        };
        ReturnsRows(svc, rows);

        var result = await sut.GetRunAsync("rid-missing", CancellationToken.None);

        Assert.That(result, Is.Null);
    }

    // ---------- GetRunsForThreadAsync (Story 2.3 Task 0.5) ----------

    [Test]
    public async Task GetRunsForThreadAsync_HappyPath_ReturnsAllRowsMatchingThreadId_GroupedByRunId()
    {
        // Two RunIds × 1 row each, both sharing the same ThreadId — models
        // a 2-step workflow run where each Run AI Agent step produces its
        // own (RunId, shared-ThreadId) tuple per Adam's PR-Upstream-N design.
        var (sut, svc, _) = CreateSutWithLogger();
        var rows = new[]
        {
            Row(runIdValue: "rid-step-1", threadIdValue: "tid-workflow", startTime: BaseUtc),
            Row(runIdValue: "rid-step-2", threadIdValue: "tid-workflow", startTime: BaseUtc.AddMinutes(1)),
            Row(runIdValue: "rid-other", threadIdValue: "tid-different-workflow", startTime: BaseUtc.AddMinutes(2)),
        };
        ReturnsRows(svc, rows);

        var result = await sut.GetRunsForThreadAsync("tid-workflow", CancellationToken.None);

        Assert.That(result, Has.Count.EqualTo(2));
        // Ordered by StartedUtc DESC — step-2 first.
        Assert.That(result[0].RunId, Is.EqualTo("rid-step-2"));
        Assert.That(result[1].RunId, Is.EqualTo("rid-step-1"));
        // Both records carry the shared ThreadId.
        Assert.That(result[0].ThreadId, Is.EqualTo("tid-workflow"));
        Assert.That(result[1].ThreadId, Is.EqualTo("tid-workflow"));
    }

    [Test]
    public async Task GetRunsForThreadAsync_FiltersForeignThreadIds_AndRowsWithNullMetadata()
    {
        // Three foreign-thread rows + 1 null-metadata row + 1 matching row.
        // Only the matching row should appear; foreign rows / pre-Fork-(i)
        // null-Metadata rows are silently filtered out.
        var (sut, svc, _) = CreateSutWithLogger();
        var rows = new[]
        {
            Row(runIdValue: "rid-target", threadIdValue: "tid-match"),
            Row(runIdValue: "rid-other-a", threadIdValue: "tid-foreign-a"),
            Row(runIdValue: "rid-other-b", threadIdValue: "tid-foreign-b"),
            Row(metadataNull: true),  // pre-Fork-(i) audit row, no Metadata
            Row(runIdValue: "rid-other-c", threadIdValue: "tid-foreign-c"),
        };
        ReturnsRows(svc, rows);

        var result = await sut.GetRunsForThreadAsync("tid-match", CancellationToken.None);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].RunId, Is.EqualTo("rid-target"));
    }

    [TestCase("")]
    [TestCase(null)]
    public async Task GetRunsForThreadAsync_NullOrEmptyThreadId_ReturnsEmptyList_WithoutHittingUpstream(string? threadId)
    {
        var (sut, svc, _) = CreateSutWithLogger();

        var result = await sut.GetRunsForThreadAsync(threadId!, CancellationToken.None);

        Assert.That(result, Is.Empty);
        // Fast-fail short-circuits BEFORE any upstream IO.
        await svc.DidNotReceiveWithAnyArgs().GetAuditLogsPagedAsync(default!, default, default, default);
    }

    [Test]
    public async Task GetRunsForThreadAsync_AuditLogServiceThrows_ReturnsEmptyAndLogsWarning()
    {
        var auditLogService = Substitute.For<IAIAuditLogService>();
        auditLogService.GetAuditLogsPagedAsync(
                Arg.Any<AIAuditLogFilter>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<(IEnumerable<AIAuditLog>, int)>>(_ => throw new InvalidOperationException("boom"));
        var logger = Substitute.For<ILogger<AgentRunReader>>();
        var sut = new AgentRunReader(
            auditLogService,
            Options.Create(new AgentMemoryOptions()),
            logger);

        var result = await sut.GetRunsForThreadAsync("tid-any", CancellationToken.None);

        Assert.That(result, Is.Empty);
        logger.Received().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<InvalidOperationException>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Test]
    public void GetRunsForThreadAsync_AuditLogServiceCancels_RethrowsOperationCanceledException()
    {
        // Pins the `catch when (ex is not OperationCanceledException)` rethrow clause —
        // a regression that drops the `when` filter would swallow cancellation and the
        // NFR-R3 graceful-degradation contract would silently mask user-initiated cancels.
        var auditLogService = Substitute.For<IAIAuditLogService>();
        auditLogService.GetAuditLogsPagedAsync(
                Arg.Any<AIAuditLogFilter>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<(IEnumerable<AIAuditLog>, int)>>(_ => throw new OperationCanceledException());
        var sut = new AgentRunReader(
            auditLogService,
            Options.Create(new AgentMemoryOptions()),
            Substitute.For<ILogger<AgentRunReader>>());

        Assert.ThrowsAsync<OperationCanceledException>(
            () => sut.GetRunsForThreadAsync("tid-any", CancellationToken.None));
    }

    // ---------- NFR-R3 graceful degradation ----------

    [Test]
    public async Task GetRecentRunsForAgentAsync_AuditLogServiceThrows_ReturnsEmptyAndLogsWarning()
    {
        var auditLogService = Substitute.For<IAIAuditLogService>();
        auditLogService.GetAuditLogsPagedAsync(
                Arg.Any<AIAuditLogFilter>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<(IEnumerable<AIAuditLog>, int)>>(_ => throw new InvalidOperationException("boom"));
        var logger = Substitute.For<ILogger<AgentRunReader>>();
        var sut = new AgentRunReader(
            auditLogService,
            Options.Create(new AgentMemoryOptions()),
            logger);

        var result = await sut.GetRecentRunsForAgentAsync(SampleAgentId, take: 10, CancellationToken.None);

        Assert.That(result, Is.Empty);
        logger.Received().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<InvalidOperationException>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Test]
    public async Task GetRunAsync_AuditLogServiceThrows_ReturnsNullAndLogsWarning()
    {
        var auditLogService = Substitute.For<IAIAuditLogService>();
        auditLogService.GetAuditLogsPagedAsync(
                Arg.Any<AIAuditLogFilter>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<(IEnumerable<AIAuditLog>, int)>>(_ => throw new InvalidOperationException("boom"));
        var logger = Substitute.For<ILogger<AgentRunReader>>();
        var sut = new AgentRunReader(
            auditLogService,
            Options.Create(new AgentMemoryOptions()),
            logger);

        var result = await sut.GetRunAsync("rid-any", CancellationToken.None);

        Assert.That(result, Is.Null);
        logger.Received().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<InvalidOperationException>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Test]
    public void GetRecentRunsForAgentAsync_OperationCancelled_PropagatesNotSwallowed()
    {
        var auditLogService = Substitute.For<IAIAuditLogService>();
        auditLogService.GetAuditLogsPagedAsync(
                Arg.Any<AIAuditLogFilter>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<(IEnumerable<AIAuditLog>, int)>>(_ => throw new OperationCanceledException());
        var sut = CreateSut(auditLogService);

        Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await sut.GetRecentRunsForAgentAsync(SampleAgentId, take: 10, CancellationToken.None));
    }

    // ---------- Worst-status precedence (AC3 — 6-row mapping contract) ----------

    // Empirical evidence at AR28: only Failed > Succeeded was exercised against real
    // data (0-c § AC1.e). The other four enum values are pinned via mocked rows
    // covering all pairwise precedences. Codex point 4: "tests cover all six values
    // via mocked AIAuditLog rows, not via runtime proof".
    private static readonly object[] WorstStatusCases =
    {
        new object[]
        {
            new[] { AIAuditLogStatus.Succeeded },
            AgentRunStatus.Succeeded,
        },
        new object[]
        {
            new[] { AIAuditLogStatus.Running },
            AgentRunStatus.Running,
        },
        new object[]
        {
            new[] { AIAuditLogStatus.PartialSuccess, AIAuditLogStatus.Succeeded },
            AgentRunStatus.PartialSuccess,
        },
        new object[]
        {
            new[] { AIAuditLogStatus.Cancelled, AIAuditLogStatus.PartialSuccess, AIAuditLogStatus.Succeeded },
            AgentRunStatus.Cancelled,
        },
        new object[]
        {
            new[] { AIAuditLogStatus.Failed, AIAuditLogStatus.Cancelled, AIAuditLogStatus.Succeeded },
            AgentRunStatus.Failed,
        },
        new object[]
        {
            new[]
            {
                AIAuditLogStatus.Blocked,
                AIAuditLogStatus.Failed,
                AIAuditLogStatus.Cancelled,
                AIAuditLogStatus.PartialSuccess,
                AIAuditLogStatus.Running,
                AIAuditLogStatus.Succeeded,
            },
            AgentRunStatus.Blocked,
        },
    };

    [TestCaseSource(nameof(WorstStatusCases))]
    public async Task WorstStatusPrecedence_PinsAllSixValues(AIAuditLogStatus[] groupStatuses, AgentRunStatus expected)
    {
        var (sut, svc, _) = CreateSutWithLogger();

        // All rows share the same RunId so the GROUP BY puts them in one group.
        // Per DRIFT-NEW-3 this is structurally rare in v0.1 (1 chat call = 1 row =
        // 1 RunId), but the precedence rule is exercised across the future-proof
        // multi-row seam.
        var rows = groupStatuses.Select((status, i) => Row(
            runIdValue: "rid-pinned",
            threadIdValue: "tid-1",
            status: status,
            startTime: BaseUtc.AddSeconds(i))).ToArray();
        ReturnsRows(svc, rows);

        var result = await sut.GetRecentRunsForAgentAsync(SampleAgentId, take: 10, CancellationToken.None);

        Assert.That(result, Has.Count.EqualTo(1), "all rows share one RunId, one group expected");
        Assert.That(result[0].AggregateStatus, Is.EqualTo(expected));
    }

    // ---------- Aggregation seams (MIN/MAX/SUM/Joined for multi-row groups) ----------

    [Test]
    public async Task ProjectGroupToRecord_MultiRowGroup_AppliesMinMaxSumJoinedAggregations()
    {
        // Future-proof seam coverage (architecture v1 lines 162-166): the
        // MIN/MAX/SUM/Joined rules must work if upstream emits multi-row-per-RunId
        // groups (PR-Upstream-3 candidate). In v0.1 this is structurally unreachable
        // through the Copilot path — exercised via mocked rows sharing one RunId.
        var (sut, svc, _) = CreateSutWithLogger();
        var rows = new[]
        {
            Row(runIdValue: "rid-multi", threadIdValue: "tid-1",
                startTime: BaseUtc.AddSeconds(20), endTime: BaseUtc.AddSeconds(30),
                inputTokens: 100, outputTokens: 20,
                prompt: "p-second", response: "r-second",
                userId: "user-2", traceId: "trace-2"),
            Row(runIdValue: "rid-multi", threadIdValue: "tid-1",
                startTime: BaseUtc.AddSeconds(0), endTime: BaseUtc.AddSeconds(10),
                inputTokens: 50, outputTokens: 5,
                prompt: "p-first", response: "r-first",
                userId: "user-1", traceId: "trace-1",
                errorMessage: "first-err"),
            Row(runIdValue: "rid-multi", threadIdValue: "tid-1",
                startTime: BaseUtc.AddSeconds(40), endTime: BaseUtc.AddSeconds(50),
                inputTokens: 200, outputTokens: 30,
                prompt: "p-third", response: "r-third",
                userId: "user-3", traceId: "trace-3"),
        };
        ReturnsRows(svc, rows);

        var result = await sut.GetRecentRunsForAgentAsync(SampleAgentId, take: 10, CancellationToken.None);

        Assert.That(result, Has.Count.EqualTo(1));
        var record = result[0];
        Assert.Multiple(() =>
        {
            Assert.That(record.StartedUtc, Is.EqualTo(BaseUtc.AddSeconds(0)), "StartedUtc = MIN(StartTime)");
            Assert.That(record.CompletedUtc, Is.EqualTo(BaseUtc.AddSeconds(50)), "CompletedUtc = MAX(EndTime)");
            Assert.That(record.TokenCountInput, Is.EqualTo(350), "TokenCountInput = SUM(InputTokens)");
            Assert.That(record.TokenCountOutput, Is.EqualTo(55), "TokenCountOutput = SUM(OutputTokens)");
            Assert.That(record.Error, Is.EqualTo("first-err"),
                "Error = first non-null ErrorMessage ordered by StartTime ascending");
            Assert.That(record.PromptSnapshotJoined, Is.EqualTo("p-first\n\n---\n\np-second\n\n---\n\np-third"),
                "PromptSnapshotJoined = ordered concatenation by StartTime ascending");
            Assert.That(record.ResponseSnapshotJoined, Is.EqualTo("r-first\n\n---\n\nr-second\n\n---\n\nr-third"),
                "ResponseSnapshotJoined = ordered concatenation by StartTime ascending");
            Assert.That(record.UserId, Is.EqualTo("user-1"),
                "UserId = first non-null UserId ordered by StartTime ascending");
            Assert.That(record.TraceId, Is.EqualTo("trace-1"),
                "TraceId = first non-null TraceId ordered by StartTime ascending");
        });
    }
}
