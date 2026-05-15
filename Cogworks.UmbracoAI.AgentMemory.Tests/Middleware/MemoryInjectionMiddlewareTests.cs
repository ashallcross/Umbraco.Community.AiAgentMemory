using Cogworks.UmbracoAI.AgentMemory.Configuration;
using Cogworks.UmbracoAI.AgentMemory.Feedback;
using Cogworks.UmbracoAI.AgentMemory.Memory;
using Cogworks.UmbracoAI.AgentMemory.Middleware;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.AI.Core.RuntimeContext;

namespace Cogworks.UmbracoAI.AgentMemory.Tests.Middleware;

/// <summary>
/// Story 3.3 — MemoryInjectionMiddleware unit tests (AC8.1-AC8.12).
///
/// Covers the per-call wrapper produced by
/// <see cref="MemoryInjectionChatMiddleware.Apply"/>:
/// runtime-context AgentId resolution, opt-in gate, retriever invocation,
/// system-message prepending, FR30 hot-reload, NFR-R3 graceful degradation,
/// cancellation discipline, and streaming parity.
///
/// Audit-log outcome verification (FR25 + FR43 — <c>PromptSnapshot</c> contains
/// <c>"Lessons from past runs"</c>) lives at the AC9.b manual gate, NOT here.
/// </summary>
[TestFixture]
public class MemoryInjectionMiddlewareTests
{
    private const string AgentIdKey = "Umbraco.AI.Agent.AgentId";

    private static readonly Guid AgentA = new("11111111-1111-1111-1111-111111111111");

    private FakeInnerChatClient _innerClient = null!;
    private IMemoryRetriever _retriever = null!;
    private IAIRuntimeContextAccessor _runtimeContextAccessor = null!;
    private AgentMemoryOptions _options = null!;
    private TestOptionsMonitor<AgentMemoryOptions> _optionsMonitor = null!;
    private ILogger<MemoryInjectionMiddleware> _logger = null!;

    [SetUp]
    public void SetUp()
    {
        _innerClient = new FakeInnerChatClient();
        _retriever = Substitute.For<IMemoryRetriever>();
        _runtimeContextAccessor = Substitute.For<IAIRuntimeContextAccessor>();
        _options = new AgentMemoryOptions
        {
            TopKMemories = 5,
            EnabledAgents = new List<Guid>(),
        };
        _optionsMonitor = new TestOptionsMonitor<AgentMemoryOptions>(_options);
        _logger = Substitute.For<ILogger<MemoryInjectionMiddleware>>();
    }

    [TearDown]
    public void TearDown()
    {
        _innerClient?.Dispose();
    }

    private MemoryInjectionMiddleware CreateSut()
        => new(_innerClient, _retriever, _runtimeContextAccessor, _optionsMonitor, _logger);

    private void SetRuntimeContext(Guid? agentId)
    {
        var context = new AIRuntimeContext(Array.Empty<AIRequestContextItem>());
        if (agentId.HasValue)
        {
            context.SetValue(AgentIdKey, agentId.Value);
        }
        _runtimeContextAccessor.Context.Returns(context);
    }

    private static MemoryEntry Memory(
        string runId = "abc12345-1111",
        string summary = "Some past lesson",
        FeedbackScore? score = FeedbackScore.ThumbsUp,
        string? comment = "useful")
        => new(runId, summary, score, comment, DateTime.UtcNow, 0.5);

    // -----------------------------------------------------------------
    // Test 1 (AC3) — happy path: opted-in + non-empty retrieval injects
    // -----------------------------------------------------------------
    [Test]
    public async Task GetResponseAsync_OptedInAgent_NonEmptyRetrieval_PrependsSystemMessage_AndCallsInner()
    {
        _options.EnabledAgents.Add(AgentA);
        SetRuntimeContext(AgentA);
        var memories = new[]
        {
            Memory(runId: "run00001-aaaa", summary: "summary-one", score: FeedbackScore.ThumbsUp, comment: "first lesson"),
            Memory(runId: "run00002-bbbb", summary: "summary-two", score: FeedbackScore.ThumbsDown, comment: null),
        };
        _retriever.RetrieveSimilarAsync(AgentA, null, Arg.Any<IReadOnlyList<ChatMessage>>(), 5, Arg.Any<CancellationToken>())
            .Returns(memories);

        var sut = CreateSut();
        var inbound = new List<ChatMessage>
        {
            new(ChatRole.User, "previous turn"),
            new(ChatRole.User, "hi"),
        };

        await sut.GetResponseAsync(inbound);

        await _retriever.Received(1).RetrieveSimilarAsync(
            AgentA, null, Arg.Any<IReadOnlyList<ChatMessage>>(), 5, Arg.Any<CancellationToken>());
        var captured = _innerClient.LastReceivedMessages;
        Assert.That(captured, Is.Not.Null);
        Assert.That(captured!.Count, Is.EqualTo(3),
            "Inner client should receive 1 prepended system message + the original 2 user messages.");
        Assert.That(captured[0].Role, Is.EqualTo(ChatRole.System));
        Assert.That(captured[0].Text, Does.StartWith("Lessons from past runs:"));
        Assert.That(captured[0].Text, Does.Contain("run00001"));
        Assert.That(captured[0].Text, Does.Contain("summary-one"));
        Assert.That(captured[0].Text, Does.Contain("first lesson"));
        Assert.That(captured[0].Text, Does.Contain("\U0001F44D")); // 👍
        Assert.That(captured[0].Text, Does.Contain("\U0001F44E")); // 👎
    }

    // -----------------------------------------------------------------
    // Test 2 (AC5) — opted-in + empty retrieval: pass-through unchanged
    // -----------------------------------------------------------------
    [Test]
    public async Task GetResponseAsync_OptedInAgent_EmptyRetrieval_PassesThroughUnchanged_NoSystemMessage()
    {
        _options.EnabledAgents.Add(AgentA);
        SetRuntimeContext(AgentA);
        _retriever.RetrieveSimilarAsync(AgentA, null, Arg.Any<IReadOnlyList<ChatMessage>>(), 5, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<MemoryEntry>());

        var sut = CreateSut();
        var inbound = new List<ChatMessage> { new(ChatRole.User, "hello") };

        await sut.GetResponseAsync(inbound);

        await _retriever.Received(1).RetrieveSimilarAsync(
            AgentA, null, Arg.Any<IReadOnlyList<ChatMessage>>(), 5, Arg.Any<CancellationToken>());
        var captured = _innerClient.LastReceivedMessages;
        Assert.That(captured!.Count, Is.EqualTo(1), "Inner client must see the original list unchanged when retriever returns empty.");
        Assert.That(captured[0].Role, Is.EqualTo(ChatRole.User));
        // AC8.2 — Reason discriminator pinned at the Debug log surface.
        AssertLoggedReason(LogLevel.Debug, "RetrieverReturnedEmpty");
    }

    // -----------------------------------------------------------------
    // Test 3 (AC2.b / NFR-R4 LOAD-BEARING) — opted-out: zero retriever calls
    // -----------------------------------------------------------------
    [Test]
    public async Task GetResponseAsync_OptedOutAgent_PassesThroughUnchanged_ZeroRetrieverCalls()
    {
        // _options.EnabledAgents stays empty (default).
        SetRuntimeContext(AgentA);

        var sut = CreateSut();
        var inbound = new List<ChatMessage> { new(ChatRole.User, "no memory please") };

        await sut.GetResponseAsync(inbound);

        await _retriever.DidNotReceiveWithAnyArgs().RetrieveSimilarAsync(
            default, default, default!, default, default);
        var captured = _innerClient.LastReceivedMessages;
        Assert.That(captured!.Count, Is.EqualTo(1),
            "Opted-out agents must see ZERO injected messages (NFR-R4 LOAD-BEARING).");
        Assert.That(captured[0].Role, Is.EqualTo(ChatRole.User));
        // AC8.3 — Reason discriminator pinned at the Debug log surface.
        AssertLoggedReason(LogLevel.Debug, "AgentNotOptedIn");
    }

    // -----------------------------------------------------------------
    // Test 4 (AC2.a) — no AgentId in runtime context: pass-through
    // -----------------------------------------------------------------
    [Test]
    public async Task GetResponseAsync_NoAgentIdInRuntimeContext_PassesThroughUnchanged_ZeroRetrieverCalls()
    {
        // Active context but NO AgentId set — TryGetValue returns false.
        SetRuntimeContext(agentId: null);

        var sut = CreateSut();
        var inbound = new List<ChatMessage> { new(ChatRole.User, "programmatic call") };

        await sut.GetResponseAsync(inbound);

        await _retriever.DidNotReceiveWithAnyArgs().RetrieveSimilarAsync(
            default, default, default!, default, default);
        var captured = _innerClient.LastReceivedMessages;
        Assert.That(captured!.Count, Is.EqualTo(1));
        // AC8.4 — Reason discriminator pinned at the Debug log surface.
        AssertLoggedReason(LogLevel.Debug, "NoAgentIdInRuntimeContext");
    }

    // -----------------------------------------------------------------
    // Test 5 (AC4 / FR30) — hot-reload mid-test via IOptionsMonitor
    // -----------------------------------------------------------------
    [Test]
    public async Task GetResponseAsync_HotReload_NewlyEnabledAgent_NextCallInjects()
    {
        // Start: empty EnabledAgents list ⇒ opted out, no injection.
        SetRuntimeContext(AgentA);
        _retriever.RetrieveSimilarAsync(AgentA, null, Arg.Any<IReadOnlyList<ChatMessage>>(), 5, Arg.Any<CancellationToken>())
            .Returns(new[] { Memory(runId: "after-add") });

        var sut = CreateSut();
        await sut.GetResponseAsync(new List<ChatMessage> { new(ChatRole.User, "call 1") });

        var firstCallMessages = _innerClient.LastReceivedMessages!;
        Assert.That(firstCallMessages.Count, Is.EqualTo(1),
            "Pre-mutation call must NOT inject because EnabledAgents is empty.");
        await _retriever.DidNotReceiveWithAnyArgs().RetrieveSimilarAsync(default, default, default!, default, default);

        // Mutate options; next CurrentValue read picks up the new list (FR30
        // hot-reload contract — the middleware re-reads CurrentValue per call).
        var nextOptions = new AgentMemoryOptions
        {
            TopKMemories = 5,
            EnabledAgents = new List<Guid> { AgentA },
        };
        _optionsMonitor.SetCurrentValue(nextOptions);

        await sut.GetResponseAsync(new List<ChatMessage> { new(ChatRole.User, "call 2") });

        var secondCallMessages = _innerClient.LastReceivedMessages!;
        Assert.That(secondCallMessages.Count, Is.EqualTo(2),
            "Post-mutation call must inject because EnabledAgents now contains AgentA.");
        Assert.That(secondCallMessages[0].Role, Is.EqualTo(ChatRole.System));
        await _retriever.Received(1).RetrieveSimilarAsync(
            AgentA, null, Arg.Any<IReadOnlyList<ChatMessage>>(), 5, Arg.Any<CancellationToken>());
    }

    // -----------------------------------------------------------------
    // Test 6 (AC2.c defensive depth-in-defence) — Guid.Empty
    // -----------------------------------------------------------------
    [Test]
    public async Task GetResponseAsync_GuidEmptyAgentId_PassesThroughUnchanged_LogsWarning_ZeroRetrieverCalls()
    {
        SetRuntimeContext(Guid.Empty);

        var sut = CreateSut();
        await sut.GetResponseAsync(new List<ChatMessage> { new(ChatRole.User, "validator-bypass scenario") });

        await _retriever.DidNotReceiveWithAnyArgs().RetrieveSimilarAsync(
            default, default, default!, default, default);
        Assert.That(_innerClient.LastReceivedMessages!.Count, Is.EqualTo(1));

        // P3 — pin Reason to discriminate from other Warning paths (retriever-throws, EnabledAgents-null).
        AssertLoggedReason(LogLevel.Warning, "AgentIdGuidEmpty");
    }

    // -----------------------------------------------------------------
    // Test 7 (AC6) — streaming parity
    // -----------------------------------------------------------------
    [Test]
    public async Task GetStreamingResponseAsync_OptedInAgent_NonEmptyRetrieval_PrependsSystemMessage_AndYieldsAllUpdates()
    {
        _options.EnabledAgents.Add(AgentA);
        SetRuntimeContext(AgentA);
        _retriever.RetrieveSimilarAsync(AgentA, null, Arg.Any<IReadOnlyList<ChatMessage>>(), 5, Arg.Any<CancellationToken>())
            .Returns(new[] { Memory(runId: "stream001") });

        _innerClient.StreamingUpdates = new List<ChatResponseUpdate>
        {
            new(ChatRole.Assistant, "chunk-1"),
            new(ChatRole.Assistant, "chunk-2"),
        };

        var sut = CreateSut();
        var collected = new List<ChatResponseUpdate>();
        await foreach (var update in sut.GetStreamingResponseAsync(new List<ChatMessage> { new(ChatRole.User, "stream me") }))
        {
            collected.Add(update);
        }

        await _retriever.Received(1).RetrieveSimilarAsync(
            AgentA, null, Arg.Any<IReadOnlyList<ChatMessage>>(), 5, Arg.Any<CancellationToken>());
        Assert.That(_innerClient.LastReceivedMessages!.Count, Is.EqualTo(2),
            "Streaming path must inject system message identically to non-streaming.");
        Assert.That(_innerClient.LastReceivedMessages[0].Role, Is.EqualTo(ChatRole.System));
        // P5 — parity with Test 1's prefix pin; verifies the LOAD-BEARING audit-log
        // marker ("Lessons from past runs:") survives the streaming path.
        Assert.That(_innerClient.LastReceivedMessages[0].Text, Does.StartWith("Lessons from past runs:"));
        Assert.That(collected.Count, Is.EqualTo(2), "All inner-yielded updates must surface to the caller.");
        Assert.That(collected[0].Text, Is.EqualTo("chunk-1"));
        Assert.That(collected[1].Text, Is.EqualTo("chunk-2"));
    }

    // -----------------------------------------------------------------
    // Test 8 (AC8.8 / NFR-R3) — retriever throws non-cancellation
    // -----------------------------------------------------------------
    [Test]
    public async Task GetResponseAsync_RetrieverThrowsNonCancellation_PassesThroughOriginalMessages_LogsWarning()
    {
        _options.EnabledAgents.Add(AgentA);
        SetRuntimeContext(AgentA);
        _retriever
            .RetrieveSimilarAsync(AgentA, null, Arg.Any<IReadOnlyList<ChatMessage>>(), 5, Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<MemoryEntry>>>(_ => throw new HttpRequestException("network down"));

        var sut = CreateSut();
        var inbound = new List<ChatMessage> { new(ChatRole.User, "carry on without memories") };

        await sut.GetResponseAsync(inbound);

        Assert.That(_innerClient.LastReceivedMessages!.Count, Is.EqualTo(1),
            "Non-cancellation retriever failure must degrade gracefully — original messages, no system message.");
        // P3 — Test 8's Warning path has no Reason= placeholder (the exception itself
        // is the discriminator); pin the "retriever threw" message marker + the
        // exception type to distinguish from other Warning paths.
        _logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("retriever threw")),
            Arg.Any<HttpRequestException>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    // -----------------------------------------------------------------
    // Test 9 (AC8.9) — OperationCanceledException propagates unwrapped
    // -----------------------------------------------------------------
    [Test]
    public void GetResponseAsync_RetrieverThrowsOperationCanceledException_PropagatesUnwrapped()
    {
        _options.EnabledAgents.Add(AgentA);
        SetRuntimeContext(AgentA);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var captured = cts.Token;
        _retriever
            .RetrieveSimilarAsync(AgentA, null, Arg.Any<IReadOnlyList<ChatMessage>>(), 5, Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<MemoryEntry>>>(_ => throw new OperationCanceledException(captured));

        var sut = CreateSut();

        Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await sut.GetResponseAsync(new List<ChatMessage> { new(ChatRole.User, "cancel me") }, cancellationToken: cts.Token));
    }

    // -----------------------------------------------------------------
    // Test 10 (AC8.10 / AC3.c) — BuildMemorySystemMessage format pin
    // (exercised end-to-end through the middleware)
    // -----------------------------------------------------------------
    [Test]
    public async Task BuildMemorySystemMessage_FormatsThreeFeedbackVariantsCorrectly()
    {
        _options.EnabledAgents.Add(AgentA);
        SetRuntimeContext(AgentA);

        var memories = new[]
        {
            new MemoryEntry("abc12345-1", "summary-up", FeedbackScore.ThumbsUp, "useful", DateTime.UtcNow, 0.5),
            new MemoryEntry("def67890-2", "summary-down", FeedbackScore.ThumbsDown, null, DateTime.UtcNow, 0.5),
            new MemoryEntry("ghi54321-3", "summary-bullet", null, "   ", DateTime.UtcNow, 0.5),
        };
        _retriever.RetrieveSimilarAsync(AgentA, null, Arg.Any<IReadOnlyList<ChatMessage>>(), 5, Arg.Any<CancellationToken>())
            .Returns(memories);

        var sut = CreateSut();
        await sut.GetResponseAsync(new List<ChatMessage> { new(ChatRole.User, "hi") });

        var body = _innerClient.LastReceivedMessages![0].Text;

        // P9 — AC8.10 demands verbatim format. Prefix tokens derived from the
        // memory's RunId via the same Math.Min(8, len) clamp the production uses,
        // so the 8-char clamp can be retuned without breaking this test.
        var prefix1 = Prefix(memories[0].RunId);
        var prefix2 = Prefix(memories[1].RunId);
        var prefix3 = Prefix(memories[2].RunId);
        var nl = Environment.NewLine;
        var expected =
            $"Lessons from past runs:{nl}" +
            $"• Run {prefix1} \U0001F44D: summary-up — \"useful\"{nl}" +
            $"• Run {prefix2} \U0001F44E: summary-down{nl}" +
            $"• Run {prefix3} •: summary-bullet{nl}";

        Assert.That(body, Is.EqualTo(expected),
            "AC8.10 LOAD-BEARING — verbatim format of the FR43 audit-log surface.");
    }

    private static string Prefix(string runId)
        => runId.AsSpan(0, Math.Min(8, runId.Length)).ToString();

    // Pins the rendered log message contains a specific `Reason={value}` structured
    // logging discriminator. NSubstitute on extension methods (LogDebug/LogWarning)
    // routes through ILogger.Log(LogLevel, EventId, TState, Exception, Formatter);
    // the TState's ToString() materialises the rendered template, so we match the
    // verbatim "Reason=<value>" substring rather than mocking the internal
    // FormattedLogValues type.
    private void AssertLoggedReason(LogLevel level, string reason)
    {
        _logger.Received(1).Log(
            level,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains($"Reason={reason}")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    // -----------------------------------------------------------------
    // Test 11 (AC8.11) — RunId shorter than 8 chars defensive clamp
    // -----------------------------------------------------------------
    [Test]
    public async Task BuildMemorySystemMessage_RunIdShorterThanEightChars_TruncatesDefensively()
    {
        _options.EnabledAgents.Add(AgentA);
        SetRuntimeContext(AgentA);

        var memories = new[]
        {
            new MemoryEntry("abc", "short-run-id-lesson", FeedbackScore.ThumbsUp, null, DateTime.UtcNow, 0.5),
        };
        _retriever.RetrieveSimilarAsync(AgentA, null, Arg.Any<IReadOnlyList<ChatMessage>>(), 5, Arg.Any<CancellationToken>())
            .Returns(memories);

        var sut = CreateSut();
        Assert.DoesNotThrowAsync(async () =>
            await sut.GetResponseAsync(new List<ChatMessage> { new(ChatRole.User, "hi") }));

        var body = _innerClient.LastReceivedMessages![0].Text;
        Assert.That(body, Does.Contain("• Run abc \U0001F44D: short-run-id-lesson"),
            "Math.Min(8, RunId.Length) must clamp to the full short string without ArgumentOutOfRangeException.");
    }

    // -----------------------------------------------------------------
    // Test 12 (AC1.a / AC8.12) — Apply factory shape pin
    // -----------------------------------------------------------------
    [Test]
    public void MemoryInjectionChatMiddleware_Apply_ReturnsWrappedDelegatingChatClient_PreservingInner()
    {
        var inner = new FakeInnerChatClient();
        var registration = new MemoryInjectionChatMiddleware(
            _retriever,
            _runtimeContextAccessor,
            _optionsMonitor,
            _logger);

        var wrapped = registration.Apply(inner);

        Assert.That(wrapped, Is.InstanceOf<MemoryInjectionMiddleware>(),
            "Apply must return a MemoryInjectionMiddleware wrapping the inbound client.");
        // Sanity — the per-call wrapper delegates through to the inner client.
        SetRuntimeContext(agentId: null); // forces a quick pass-through without retriever call
        Assert.DoesNotThrowAsync(async () =>
            await wrapped.GetResponseAsync(new List<ChatMessage> { new(ChatRole.User, "delegating chain check") }));
        Assert.That(inner.LastReceivedMessages, Is.Not.Null,
            "Wrapped client must delegate GetResponseAsync calls to the inbound IChatClient.");
    }

    // -----------------------------------------------------------------
    // Test 13 (P4) — Apply guards against a null inbound client
    // -----------------------------------------------------------------
    [Test]
    public void MemoryInjectionChatMiddleware_Apply_NullInner_Throws()
    {
        var registration = new MemoryInjectionChatMiddleware(
            _retriever,
            _runtimeContextAccessor,
            _optionsMonitor,
            _logger);

        Assert.That(() => registration.Apply(null!), Throws.ArgumentNullException);
    }

    // -----------------------------------------------------------------
    // Test 14 (P2 — code-review patch) — Context is null: pass-through
    // -----------------------------------------------------------------
    [Test]
    public async Task GetResponseAsync_RuntimeContextIsNull_PassesThroughUnchanged_ZeroRetrieverCalls()
    {
        _runtimeContextAccessor.Context.Returns((AIRuntimeContext?)null);

        var sut = CreateSut();
        var inbound = new List<ChatMessage> { new(ChatRole.User, "no active scope") };

        await sut.GetResponseAsync(inbound);

        await _retriever.DidNotReceiveWithAnyArgs().RetrieveSimilarAsync(
            default, default, default!, default, default);
        Assert.That(_innerClient.LastReceivedMessages!.Count, Is.EqualTo(1));
        AssertLoggedReason(LogLevel.Debug, "NoRuntimeContext");
    }

    // -----------------------------------------------------------------
    // Test 15 (P6 — code-review patch) — EnabledAgents null: defensive guard
    // -----------------------------------------------------------------
    [Test]
    public async Task GetResponseAsync_EnabledAgentsCollectionIsNull_PassesThroughUnchanged_LogsWarning_ZeroRetrieverCalls()
    {
        // Validator (Story 1.3) pins EnabledAgents non-null for default-bound
        // options; named-options bypass scenario simulated by mutating the field.
        _optionsMonitor.SetCurrentValue(new AgentMemoryOptions
        {
            TopKMemories = 5,
            EnabledAgents = null!, // bypass simulation
        });
        SetRuntimeContext(AgentA);

        var sut = CreateSut();
        await sut.GetResponseAsync(new List<ChatMessage> { new(ChatRole.User, "bypass scenario") });

        await _retriever.DidNotReceiveWithAnyArgs().RetrieveSimilarAsync(
            default, default, default!, default, default);
        Assert.That(_innerClient.LastReceivedMessages!.Count, Is.EqualTo(1));
        AssertLoggedReason(LogLevel.Warning, "EnabledAgentsNull");
    }

    // -----------------------------------------------------------------
    // Test 16 (P10 — code-review patch) — Summary with newlines flattens to one bullet
    // -----------------------------------------------------------------
    [Test]
    public async Task BuildMemorySystemMessage_SummaryWithNewlines_RendersAsSingleBulletLine()
    {
        _options.EnabledAgents.Add(AgentA);
        SetRuntimeContext(AgentA);

        var memories = new[]
        {
            new MemoryEntry(
                "runabc12-1",
                "line1\r\nline2\nline3",
                FeedbackScore.ThumbsUp,
                null,
                DateTime.UtcNow,
                0.5),
        };
        _retriever.RetrieveSimilarAsync(AgentA, null, Arg.Any<IReadOnlyList<ChatMessage>>(), 5, Arg.Any<CancellationToken>())
            .Returns(memories);

        var sut = CreateSut();
        await sut.GetResponseAsync(new List<ChatMessage> { new(ChatRole.User, "hi") });

        var body = _innerClient.LastReceivedMessages![0].Text;
        var nl = Environment.NewLine;
        var expected =
            $"Lessons from past runs:{nl}" +
            $"• Run runabc12 \U0001F44D: line1 line2 line3{nl}";

        Assert.That(body, Is.EqualTo(expected),
            "P10 — Summary newlines must flatten to spaces so each memory occupies a single bullet line.");
    }

    // -----------------------------------------------------------------
    // Test 17 (final review patch) — FeedbackComment with newlines flattens
    // -----------------------------------------------------------------
    [Test]
    public async Task BuildMemorySystemMessage_FeedbackCommentWithNewlines_RendersAsSingleBulletLine()
    {
        _options.EnabledAgents.Add(AgentA);
        SetRuntimeContext(AgentA);

        var memories = new[]
        {
            new MemoryEntry(
                "runcomm1-1",
                "summary",
                FeedbackScore.ThumbsDown,
                "comment line 1\r\ncomment line 2\ncomment line 3",
                DateTime.UtcNow,
                0.5),
        };
        _retriever.RetrieveSimilarAsync(AgentA, null, Arg.Any<IReadOnlyList<ChatMessage>>(), 5, Arg.Any<CancellationToken>())
            .Returns(memories);

        var sut = CreateSut();
        await sut.GetResponseAsync(new List<ChatMessage> { new(ChatRole.User, "hi") });

        var body = _innerClient.LastReceivedMessages![0].Text;
        var nl = Environment.NewLine;
        var expected =
            $"Lessons from past runs:{nl}" +
            $"• Run runcomm1 \U0001F44E: summary — \"comment line 1 comment line 2 comment line 3\"{nl}";

        Assert.That(body, Is.EqualTo(expected),
            "Final review — FeedbackComment newlines must flatten to spaces for the same bullet-list integrity as Summary.");
    }

    // ==========================================================================
    // Test doubles
    // ==========================================================================

    /// <summary>
    /// Captures the messages parameter the middleware delegates to its inner
    /// client. NSubstitute's generic-method capture surface is awkward for
    /// <see cref="IEnumerable{ChatMessage}"/> introspection; a hand-rolled
    /// double is cleaner.
    /// </summary>
    private sealed class FakeInnerChatClient : IChatClient
    {
        public List<ChatMessage>? LastReceivedMessages { get; private set; }
        public List<ChatResponseUpdate> StreamingUpdates { get; set; } = new();

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            LastReceivedMessages = messages.ToList();
            return Task.FromResult(new ChatResponse());
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LastReceivedMessages = messages.ToList();
            foreach (var update in StreamingUpdates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return update;
                await Task.Yield();
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    /// <summary>
    /// Test-fixture <see cref="IOptionsMonitor{T}"/> with mutable
    /// <see cref="CurrentValue"/> so AC4 hot-reload tests can mutate
    /// mid-test. Mirrors the Story 3.2
    /// <c>SemanticMemoryRetrieverTests.TestOptionsMonitor</c> pattern,
    /// extended with <see cref="SetCurrentValue"/>.
    /// </summary>
    private sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
        where T : class
    {
        private T _value;
        public TestOptionsMonitor(T value) => _value = value;
        public T CurrentValue => _value;
        public T Get(string? name) => _value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
        public void SetCurrentValue(T value) => _value = value;
    }
}
