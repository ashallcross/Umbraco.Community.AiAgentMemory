using Cogworks.UmbracoAI.AgentMemory.Configuration;
using Cogworks.UmbracoAI.AgentMemory.Feedback;
using Cogworks.UmbracoAI.AgentMemory.Memory;
using Cogworks.UmbracoAI.AgentMemory.Persistence.Entities;
using Cogworks.UmbracoAI.AgentMemory.Persistence.Repositories;
using Cogworks.UmbracoAI.AgentMemory.Runs;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Umbraco.AI.Core.Embeddings;
using Umbraco.AI.Core.Models;
using Umbraco.AI.Core.Profiles;
using Umbraco.AI.Search.Core.VectorStore;
using Umbraco.Cms.Core.HostedServices;

namespace Cogworks.UmbracoAI.AgentMemory.Tests.Memory;

/// <summary>
/// Unit tests for <see cref="FeedbackIndexer"/>. Drives <see cref="IFeedbackIndexer.IndexAsync"/>
/// directly against NSubstitute stubs (the queue / hosted-service plumbing is
/// covered by the Story 3.1 Task 6 integration spine). Mocks include the new
/// <see cref="IMemoryEntryRepository"/> seam introduced at Task 2a.
/// </summary>
[TestFixture]
public class FeedbackIndexerTests
{
    private static readonly DateTime FixedUtcNow = new(2026, 5, 14, 12, 0, 0, DateTimeKind.Utc);
    private const string DefaultRunId = "run-1";
    private const string DefaultAlias = "openai-embedding";

    private IBackgroundTaskQueue _queue = null!;
    private IAIVectorStore _vectorStore = null!;
    private IAIEmbeddingService _embeddingService = null!;
    private IAIProfileService _profileService = null!;
    private IAgentRunReader _runReader = null!;
    private IAgentFeedbackService _feedbackService = null!;
    private IMemoryEntryRepository _repository = null!;
    private AgentMemoryOptions _options = null!;
    private AIOptions _aiOptions = null!;
    private FakeTimeProvider _timeProvider = null!;
    private Guid _agentId;
    private Guid _profileId;
    private FeedbackIndexer _indexer = null!;

    [SetUp]
    public void SetUp()
    {
        _queue = Substitute.For<IBackgroundTaskQueue>();
        _vectorStore = Substitute.For<IAIVectorStore>();
        _embeddingService = Substitute.For<IAIEmbeddingService>();
        _profileService = Substitute.For<IAIProfileService>();
        _runReader = Substitute.For<IAgentRunReader>();
        _feedbackService = Substitute.For<IAgentFeedbackService>();
        _repository = Substitute.For<IMemoryEntryRepository>();
        _timeProvider = new FakeTimeProvider(new DateTimeOffset(FixedUtcNow, TimeSpan.Zero));
        _agentId = Guid.NewGuid();
        _profileId = Guid.NewGuid();

        _options = new AgentMemoryOptions
        {
            EmbeddingProfileAlias = DefaultAlias,
            DigestMaxChars = 500,
        };
        _aiOptions = new AIOptions();

        // Defaults: profile resolves; one run record; one feedback row.
        var profile = MakeProfile(_profileId, DefaultAlias);
        _profileService.GetProfileByAliasAsync(DefaultAlias, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AIProfile?>(profile));
        _runReader.GetRunsForThreadAsync(DefaultRunId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentRunRecord>>(new[] { MakeRun() }));
        _feedbackService.GetFeedbackForRunAsync(DefaultRunId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentRunFeedback>>(new[] { MakeFeedback("looks good") }));

        // Default embedding result.
        _embeddingService.GenerateEmbeddingAsync(
                Arg.Any<Action<AIEmbeddingBuilder>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new Embedding<float>(new float[] { 0.1f, 0.2f, 0.3f })));

        // NSubstitute default for Task<T?> is null Task — make it return a
        // proper Task<MemoryEntryEntity?>(null) so the indexer's Upsert helper
        // doesn't NRE awaiting the find call.
        _repository.FindByRunIdAndAgentIdAsync(
                Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<MemoryEntryEntity?>(null));
        _repository.AddAsync(Arg.Any<MemoryEntryEntity>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _repository.UpdateAsync(Arg.Any<MemoryEntryEntity>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _vectorStore.UpsertAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>(),
                Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<IDictionary<string, object>?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        _indexer = BuildIndexer();
    }

    private FeedbackIndexer BuildIndexer()
    {
        // Wire a real ServiceProvider so the indexer's per-call scope can
        // resolve all dependencies via GetRequiredService.
        var services = new ServiceCollection();
        services.AddSingleton(_vectorStore);
        services.AddSingleton(_embeddingService);
        services.AddSingleton(_profileService);
        services.AddSingleton(_runReader);
        services.AddSingleton(_feedbackService);
        services.AddSingleton(_repository);
        var provider = services.BuildServiceProvider();

        return new FeedbackIndexer(
            _queue,
            provider.GetRequiredService<IServiceScopeFactory>(),
            new TestOptionsMonitor<AgentMemoryOptions>(_options),
            Options.Create(_aiOptions),
            _timeProvider,
            NullLogger<FeedbackIndexer>.Instance);
    }

    private static AgentRunRecord MakeRun(
        string runId = DefaultRunId,
        string prompt = "[user] hello",
        string response = "[assistant] hi",
        Guid? agentId = null) => new(
            RunId: runId,
            AgentId: agentId ?? Guid.Empty,
            AgentVersion: 1,
            StartedUtc: FixedUtcNow.AddMinutes(-1),
            CompletedUtc: FixedUtcNow,
            AggregateStatus: AgentRunStatus.Succeeded,
            Error: null,
            PromptSnapshotJoined: prompt,
            ResponseSnapshotJoined: response,
            TokenCountInput: 10,
            TokenCountOutput: 5,
            ThreadId: runId,
            UserId: "user-1",
            TraceId: "trace-1");

    /// <summary>
    /// Constructs an <see cref="AIProfile"/> by way of reflection — its
    /// <c>Id</c> setter is internal-set (sealed type), and required members
    /// (<c>Name</c>, <c>ConnectionId</c>, <c>Capability</c>) force the object
    /// initializer to populate them up-front.
    /// </summary>
    private static AIProfile MakeProfile(Guid id, string alias)
    {
        var profile = new AIProfile
        {
            Alias = alias,
            Name = "test-profile",
            ConnectionId = Guid.NewGuid(),
            Capability = AICapability.Embedding,
        };
        typeof(AIProfile).GetProperty(nameof(AIProfile.Id))!
            .SetValue(profile, id);
        return profile;
    }

    private AgentRunFeedback MakeFeedback(
        string? comment,
        DateTime? createdUtc = null,
        Guid? createdBy = null) => new(
            Id: Guid.NewGuid(),
            RunId: DefaultRunId,
            AgentId: _agentId,
            Score: FeedbackScore.ThumbsUp,
            Comment: comment,
            CreatedBy: createdBy ?? Guid.NewGuid(),
            CreatedUtc: createdUtc ?? FixedUtcNow);

    /// <summary>
    /// Polls <see cref="FakeTimeProvider.Advance"/> while yielding control so
    /// the indexer's <c>Task.Delay(TimeSpan, TimeProvider, ct)</c> resolves.
    /// Without this pattern the inter-attempt delays would never complete
    /// (the test thread can't both block on the task AND advance the clock).
    /// </summary>
    private async Task DriveRetryTaskAsync(Task indexTask)
    {
        for (var i = 0; i < 50 && !indexTask.IsCompleted; i++)
        {
            // Real-time yield gives the indexer's continuation thread a
            // chance to register the Task.Delay callback before we advance.
            await Task.Delay(10);
            _timeProvider.Advance(TimeSpan.FromSeconds(60));
        }
        await indexTask.WaitAsync(TimeSpan.FromSeconds(10));
    }

    // ─────────────────────────────────────────────────────────────────────
    // AC1 — happy path
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public async Task IndexAsync_HappyPath_BuildsDigestEmbedsUpsertsAndPersistsEmbeddedRow()
    {
        await _indexer.IndexAsync(DefaultRunId, _agentId, CancellationToken.None);

        // Embedding called once with the joined digest text.
        // Segment order is Comment → Response → Prompt per AR35 / Story 4.2.
        await _embeddingService.Received(1).GenerateEmbeddingAsync(
            Arg.Any<Action<AIEmbeddingBuilder>>(),
            "looks good\n\n[assistant] hi\n\n[user] hello",
            Arg.Any<CancellationToken>());

        // Vector store called with the right index alias + culture null + chunk 0 + metadata.
        await _vectorStore.Received(1).UpsertAsync(
            "cogworks-agent-memory",
            Arg.Any<string>(),
            Arg.Is<string?>(c => c == null),
            0,
            Arg.Any<ReadOnlyMemory<float>>(),
            Arg.Is<IDictionary<string, object>?>(m => m != null
                && m["agentId"].ToString() == _agentId.ToString("D")
                && (string)m["runId"] == DefaultRunId),
            Arg.Any<CancellationToken>());

        // Entries row persisted with Embedded ordinal + EmbeddedUtc populated.
        await _repository.Received(1).AddAsync(
            Arg.Is<MemoryEntryEntity>(e =>
                e.RunId == DefaultRunId
                && e.AgentId == _agentId
                && e.IndexingStatus == (int)IndexingStatus.Embedded
                && e.IndexingError == null
                && e.EmbeddedUtc == FixedUtcNow
                && e.WorkspaceId == null),
            Arg.Any<CancellationToken>());
    }

    // ─────────────────────────────────────────────────────────────────────
    // AC1.c — digest truncation
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public async Task IndexAsync_DigestExceedsDigestMaxChars_TruncatesToConfiguredCap()
    {
        _options.DigestMaxChars = 100;
        var longText = new string('p', 250);
        _runReader.GetRunsForThreadAsync(DefaultRunId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentRunRecord>>(new[]
            {
                MakeRun(prompt: longText, response: longText),
            }));

        await _indexer.IndexAsync(DefaultRunId, _agentId, CancellationToken.None);

        await _repository.Received(1).AddAsync(
            Arg.Is<MemoryEntryEntity>(e => e.DigestText.Length == 100),
            Arg.Any<CancellationToken>());
    }

    // ─────────────────────────────────────────────────────────────────────
    // AC1.c — null comment segment omitted
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public async Task IndexAsync_FeedbackHasNoComment_DigestOmitsCommentSegment()
    {
        _feedbackService.GetFeedbackForRunAsync(DefaultRunId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentRunFeedback>>(new[] { MakeFeedback(comment: null) }));

        await _indexer.IndexAsync(DefaultRunId, _agentId, CancellationToken.None);

        // Comment null ⇒ digest = Response \n\n Prompt (segment order
        // Comment → Response → Prompt; null comment segment skipped) per
        // AR35 / Story 4.2.
        await _embeddingService.Received(1).GenerateEmbeddingAsync(
            Arg.Any<Action<AIEmbeddingBuilder>>(),
            "[assistant] hi\n\n[user] hello",
            Arg.Any<CancellationToken>());
    }

    // ─────────────────────────────────────────────────────────────────────
    // AC2 — realistic editorial content under truncation (AR35 / Story 4.2)
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public async Task IndexAsync_RealisticEditorialContent_DigestRetainsCommentVerbatimUnderTruncation()
    {
        // Setup mirrors Adam's Story 4.1 manual gate dry-run shape: ~3 KB
        // prompt + ~2 KB response + ~400-char editorial brand-voice comment.
        // Pre-AR35 (Prompt → Response → Comment order), the comment chopped
        // entirely. Post-AR35 (Comment → Response → Prompt), the comment
        // survives at the head of the digest verbatim.
        var prompt = new string('P', 3000);
        var response = new string('R', 2000);
        const string comment =
            "These are intentional Northwind Trails brand idioms, do not flag: " +
            "'the wild calling', 'feet on the ground', 'the long way home'. " +
            "Brand guideline: regional idioms like these are part of the voice, " +
            "not breaches per guideline #6. Please weight editorial-tone " +
            "guidelines below the explicit brand-voice register clause from " +
            "section 6 of the Northwind Trails style guide.";

        _runReader.GetRunsForThreadAsync(DefaultRunId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentRunRecord>>(new[]
            {
                MakeRun(prompt: prompt, response: response),
            }));
        _feedbackService.GetFeedbackForRunAsync(DefaultRunId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentRunFeedback>>(new[]
            {
                MakeFeedback(comment),
            }));

        await _indexer.IndexAsync(DefaultRunId, _agentId, CancellationToken.None);

        await _repository.Received(1).AddAsync(
            Arg.Is<MemoryEntryEntity>(e =>
                e.DigestText.Length == 500
                && e.DigestText.StartsWith(comment)),
            Arg.Any<CancellationToken>());
    }

    // ─────────────────────────────────────────────────────────────────────
    // AC1.a — run reader empty + multi-record collapse
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public async Task IndexAsync_RunReaderReturnsEmpty_LogsDebugAndSkipsWithoutWritingEntriesRow()
    {
        _runReader.GetRunsForThreadAsync(DefaultRunId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentRunRecord>>(Array.Empty<AgentRunRecord>()));

        await _indexer.IndexAsync(DefaultRunId, _agentId, CancellationToken.None);

        await _repository.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
        await _vectorStore.DidNotReceiveWithAnyArgs().UpsertAsync(
            default!, default!, default, default, default, default, default);
        await _embeddingService.DidNotReceiveWithAnyArgs().GenerateEmbeddingAsync(
            default(Action<AIEmbeddingBuilder>)!, default!, default);
    }

    [Test]
    public async Task IndexAsync_RunReaderReturnsMultipleRecords_UsesFirstRecordOnly()
    {
        // v0.1 single-call collapse — Locked decision #17.
        var first = MakeRun(prompt: "[user] first", response: "[assistant] first");
        var second = MakeRun(prompt: "[user] second", response: "[assistant] second");
        var third = MakeRun(prompt: "[user] third", response: "[assistant] third");
        _runReader.GetRunsForThreadAsync(DefaultRunId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentRunRecord>>(new[] { first, second, third }));

        await _indexer.IndexAsync(DefaultRunId, _agentId, CancellationToken.None);

        // Comment → Response → Prompt segment order per AR35 / Story 4.2;
        // runs[0] = first record (most-recent per StartedUtc DESC convention).
        await _embeddingService.Received(1).GenerateEmbeddingAsync(
            Arg.Any<Action<AIEmbeddingBuilder>>(),
            "looks good\n\n[assistant] first\n\n[user] first",
            Arg.Any<CancellationToken>());
    }

    // ─────────────────────────────────────────────────────────────────────
    // AC1.b — feedback service empty
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public async Task IndexAsync_FeedbackServiceReturnsEmpty_LogsDebugAndSkipsWithoutWritingEntriesRow()
    {
        _feedbackService.GetFeedbackForRunAsync(DefaultRunId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentRunFeedback>>(Array.Empty<AgentRunFeedback>()));

        await _indexer.IndexAsync(DefaultRunId, _agentId, CancellationToken.None);

        await _repository.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
        await _vectorStore.DidNotReceiveWithAnyArgs().UpsertAsync(
            default!, default!, default, default, default, default, default);
        await _embeddingService.DidNotReceiveWithAnyArgs().GenerateEmbeddingAsync(
            default(Action<AIEmbeddingBuilder>)!, default!, default);
    }

    // ─────────────────────────────────────────────────────────────────────
    // AC3 — silent no-op paths
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public async Task IndexAsync_NoEmbeddingProfileAliasConfigured_SilentlyNoOps()
    {
        _options.EmbeddingProfileAlias = null;
        _aiOptions.DefaultEmbeddingProfileAlias = null;

        await _indexer.IndexAsync(DefaultRunId, _agentId, CancellationToken.None);

        await _repository.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
        await _vectorStore.DidNotReceiveWithAnyArgs().UpsertAsync(
            default!, default!, default, default, default, default, default);
        await _embeddingService.DidNotReceiveWithAnyArgs().GenerateEmbeddingAsync(
            default(Action<AIEmbeddingBuilder>)!, default!, default);
    }

    [Test]
    public async Task IndexAsync_EmbeddingProfileAliasLookupReturnsNull_SilentlyNoOps()
    {
        _profileService.GetProfileByAliasAsync(DefaultAlias, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AIProfile?>(null));

        await _indexer.IndexAsync(DefaultRunId, _agentId, CancellationToken.None);

        await _repository.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
        await _vectorStore.DidNotReceiveWithAnyArgs().UpsertAsync(
            default!, default!, default, default, default, default, default);
        await _embeddingService.DidNotReceiveWithAnyArgs().GenerateEmbeddingAsync(
            default(Action<AIEmbeddingBuilder>)!, default!, default);
    }

    // ─────────────────────────────────────────────────────────────────────
    // AC2 — retry policy
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public async Task IndexAsync_EmbeddingThrowsTransientThenSucceeds_RetriesWithExponentialBackoff()
    {
        var callCount = 0;
        _embeddingService.GenerateEmbeddingAsync(
                Arg.Any<Action<AIEmbeddingBuilder>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                if (callCount <= 2)
                {
                    return Task.FromException<Embedding<float>>(new HttpRequestException("transient"));
                }
                return Task.FromResult(new Embedding<float>(new float[] { 0.5f }));
            });

        var indexTask = _indexer.IndexAsync(DefaultRunId, _agentId, CancellationToken.None);
        await DriveRetryTaskAsync(indexTask);

        Assert.That(callCount, Is.EqualTo(3));
        await _repository.Received(1).AddAsync(
            Arg.Is<MemoryEntryEntity>(e => e.IndexingStatus == (int)IndexingStatus.Embedded),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task IndexAsync_EmbeddingThrowsPermanently_PersistsFailedRowAndLogsError()
    {
        _embeddingService.GenerateEmbeddingAsync(
                Arg.Any<Action<AIEmbeddingBuilder>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns<Embedding<float>>(_ => throw new HttpRequestException("provider down"));

        var indexTask = _indexer.IndexAsync(DefaultRunId, _agentId, CancellationToken.None);
        await DriveRetryTaskAsync(indexTask);

        await _repository.Received(1).AddAsync(
            Arg.Is<MemoryEntryEntity>(e =>
                e.IndexingStatus == (int)IndexingStatus.Failed
                && e.EmbeddedUtc == null
                && e.EmbeddingRef == string.Empty
                && e.IndexingError != null
                && e.IndexingError.Contains("provider down")),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task IndexAsync_VectorStoreUpsertThrowsTransientThenSucceeds_RetriesWithinSameEnvelope()
    {
        var upsertCalls = 0;
        _vectorStore
            .When(v => v.UpsertAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<int>(),
                Arg.Any<ReadOnlyMemory<float>>(),
                Arg.Any<IDictionary<string, object>?>(),
                Arg.Any<CancellationToken>()))
            .Do(_ =>
            {
                upsertCalls++;
                if (upsertCalls <= 2)
                {
                    throw new InvalidOperationException("vector store glitch");
                }
            });

        var indexTask = _indexer.IndexAsync(DefaultRunId, _agentId, CancellationToken.None);
        await DriveRetryTaskAsync(indexTask);

        // AC2 atomic-budget — re-embed each attempt because state isn't persisted.
        await _embeddingService.Received(3).GenerateEmbeddingAsync(
            Arg.Any<Action<AIEmbeddingBuilder>>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
        Assert.That(upsertCalls, Is.EqualTo(3));
        await _repository.Received(1).AddAsync(
            Arg.Is<MemoryEntryEntity>(e => e.IndexingStatus == (int)IndexingStatus.Embedded),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task IndexAsync_VectorStoreUpsertThrowsPermanently_PersistsFailedRow()
    {
        _vectorStore
            .When(v => v.UpsertAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<int>(),
                Arg.Any<ReadOnlyMemory<float>>(),
                Arg.Any<IDictionary<string, object>?>(),
                Arg.Any<CancellationToken>()))
            .Do(_ => throw new InvalidOperationException("vector store offline"));

        var indexTask = _indexer.IndexAsync(DefaultRunId, _agentId, CancellationToken.None);
        await DriveRetryTaskAsync(indexTask);

        await _repository.Received(1).AddAsync(
            Arg.Is<MemoryEntryEntity>(e =>
                e.IndexingStatus == (int)IndexingStatus.Failed
                && e.IndexingError != null
                && e.IndexingError.Contains("vector store offline")),
            Arg.Any<CancellationToken>());
    }

    // ─────────────────────────────────────────────────────────────────────
    // AC2 — OperationCanceledException propagation
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public async Task IndexAsync_OperationCanceledException_FromEmbed_PropagatesUnwrapped_NeverPersistsFailedRow()
    {
        _embeddingService.GenerateEmbeddingAsync(
                Arg.Any<Action<AIEmbeddingBuilder>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns<Embedding<float>>(_ => throw new OperationCanceledException());

        Assert.ThrowsAsync<OperationCanceledException>(
            () => _indexer.IndexAsync(DefaultRunId, _agentId, CancellationToken.None));

        await _repository.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
    }

    [Test]
    public async Task IndexAsync_OperationCanceledException_FromUpsert_PropagatesUnwrapped()
    {
        _vectorStore
            .When(v => v.UpsertAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<int>(),
                Arg.Any<ReadOnlyMemory<float>>(),
                Arg.Any<IDictionary<string, object>?>(),
                Arg.Any<CancellationToken>()))
            .Do(_ => throw new OperationCanceledException());

        Assert.ThrowsAsync<OperationCanceledException>(
            () => _indexer.IndexAsync(DefaultRunId, _agentId, CancellationToken.None));

        await _repository.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
    }

    // ─────────────────────────────────────────────────────────────────────
    // AC1 — supersede / multi-editor collapse
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public async Task IndexAsync_ExistingEntriesRowForRunIdAgentId_UpdatesInPlaceInsteadOfDuplicating()
    {
        var existingId = Guid.NewGuid();
        var existing = new MemoryEntryEntity
        {
            Id = existingId,
            RunId = DefaultRunId,
            AgentId = _agentId,
            WorkspaceId = null,
            DigestText = "stale",
            EmbeddingRef = "stale-doc",
            IndexingStatus = (int)IndexingStatus.Embedded,
            IndexingError = null,
            EmbeddedUtc = FixedUtcNow.AddMinutes(-10),
            CreatedUtc = FixedUtcNow.AddMinutes(-10),
        };
        _repository.FindByRunIdAndAgentIdAsync(DefaultRunId, _agentId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<MemoryEntryEntity?>(existing));

        await _indexer.IndexAsync(DefaultRunId, _agentId, CancellationToken.None);

        // Comment → Response → Prompt segment order per AR35 / Story 4.2.
        await _repository.Received(1).UpdateAsync(
            Arg.Is<MemoryEntryEntity>(e =>
                e.Id == existingId
                && e.DigestText == "looks good\n\n[assistant] hi\n\n[user] hello"
                && e.EmbeddingRef != "stale-doc"
                && e.IndexingStatus == (int)IndexingStatus.Embedded),
            Arg.Any<CancellationToken>());
        await _repository.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
    }

    [Test]
    public async Task IndexAsync_MultiEditorOnSameRun_CollapsesToOneEntryWithMostRecentFeedbackWinning()
    {
        // CreatedUtc DESC ordering per Story 2.1 contract — feedback[0] wins.
        var sarah = MakeFeedback(comment: "sarah's comment", createdUtc: FixedUtcNow);
        var marcus = MakeFeedback(comment: "marcus's comment", createdUtc: FixedUtcNow.AddMinutes(-5));
        _feedbackService.GetFeedbackForRunAsync(DefaultRunId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentRunFeedback>>(new[] { sarah, marcus }));

        await _indexer.IndexAsync(DefaultRunId, _agentId, CancellationToken.None);

        // Comment now leads the digest per AR35 / Story 4.2 (was EndsWith
        // pre-AR35; comment trailed prompt + response under old segment order).
        await _repository.Received(1).AddAsync(
            Arg.Is<MemoryEntryEntity>(e => e.DigestText.StartsWith("sarah's comment")),
            Arg.Any<CancellationToken>());
    }

    // ─────────────────────────────────────────────────────────────────────
    // Defensive input validation at the public IFeedbackIndexer surface
    // ─────────────────────────────────────────────────────────────────────

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void EnqueueIndex_NullOrWhitespaceRunId_ThrowsArgumentException(string? runId)
    {
        // ArgumentException.ThrowIfNullOrWhiteSpace throws ArgumentNullException
        // for null, ArgumentException for whitespace — both inherit from
        // ArgumentException, so Assert.Catch covers either.
        Assert.Catch<ArgumentException>(() => _indexer.EnqueueIndex(runId!, _agentId));
    }

    [Test]
    public void EnqueueIndex_GuidEmptyAgentId_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() => _indexer.EnqueueIndex(DefaultRunId, Guid.Empty));
        Assert.That(ex!.ParamName, Is.EqualTo("agentId"));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void IndexAsync_NullOrWhitespaceRunId_ThrowsArgumentException(string? runId)
    {
        Assert.CatchAsync<ArgumentException>(
            () => _indexer.IndexAsync(runId!, _agentId, CancellationToken.None));
    }

    [Test]
    public void IndexAsync_GuidEmptyAgentId_ThrowsArgumentException()
    {
        var ex = Assert.ThrowsAsync<ArgumentException>(
            () => _indexer.IndexAsync(DefaultRunId, Guid.Empty, CancellationToken.None));
        Assert.That(ex!.ParamName, Is.EqualTo("agentId"));
    }

    // ─────────────────────────────────────────────────────────────────────
    // Best-effort durability — orphan vector hazard (post-embed DB failure)
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public async Task IndexAsync_EmbedUpsertSucceeds_ButEntriesRowWriteThrows_SwallowsExceptionWithoutBubbling()
    {
        // Pins the v0.1 "best-effort durability contract": when the vector
        // store upsert succeeds but the entries-row write subsequently throws
        // (e.g. DB transient), the indexer's outer catch logs a warning and
        // returns without propagating. The vector exists at its deterministic
        // documentId; no entries row references it. v0.2 durable-queue
        // candidate (see deferred-work.md).
        _repository.AddAsync(Arg.Any<MemoryEntryEntity>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("simulated DB failure"));

        // Must not throw — outer catch (Exception ex) absorbs.
        await _indexer.IndexAsync(DefaultRunId, _agentId, CancellationToken.None);

        // The vector upsert DID happen — orphan vector left in place.
        await _vectorStore.Received(1).UpsertAsync(
            "cogworks-agent-memory",
            Arg.Any<string>(),
            Arg.Is<string?>(c => c == null),
            0,
            Arg.Any<ReadOnlyMemory<float>>(),
            Arg.Any<IDictionary<string, object>?>(),
            Arg.Any<CancellationToken>());
        // AddAsync was attempted (and threw).
        await _repository.Received(1).AddAsync(
            Arg.Any<MemoryEntryEntity>(), Arg.Any<CancellationToken>());
    }

    // ─────────────────────────────────────────────────────────────────────
    // AC8 — universal indexing (EnabledAgents does NOT gate capture)
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public async Task IndexAsync_AgentNotOnEnabledAgentsList_StillPersistsEntriesRowAndUpsertsVector()
    {
        _options.EnabledAgents = new List<Guid>();

        await _indexer.IndexAsync(DefaultRunId, _agentId, CancellationToken.None);

        await _vectorStore.Received(1).UpsertAsync(
            "cogworks-agent-memory",
            Arg.Any<string>(),
            Arg.Is<string?>(c => c == null),
            0,
            Arg.Any<ReadOnlyMemory<float>>(),
            Arg.Any<IDictionary<string, object>?>(),
            Arg.Any<CancellationToken>());
        await _repository.Received(1).AddAsync(
            Arg.Any<MemoryEntryEntity>(), Arg.Any<CancellationToken>());
    }

    // ═════════════════════════════════════════════════════════════════════
    // Story 4.12 — selectedRunId-first resolution (AC4.2; AC5 indexer tests)
    // ═════════════════════════════════════════════════════════════════════

    [Test]
    public async Task IndexAsync_PerIterationRunId_UsesGetRunAsyncAndIndexesSelectedRun()
    {
        // Picker submission path. AgentFeedbackController passes the per-iteration
        // RunId (rid-step-2) instead of the ThreadId. FeedbackIndexer probes
        // GetRunAsync(rid-step-2) FIRST + must use THAT iteration's prompt/response
        // for the digest. Critically: it must NOT fall back to
        // GetRunsForThreadAsync(rid-step-2)/runs[0] when GetRunAsync resolves.
        const string selectedRunId = "rid-step-2";
        var selectedRun = MakeRun(
            runId: selectedRunId,
            prompt: "[user] iteration-2 prompt body",
            response: "[assistant] iteration-2 response body",
            agentId: _agentId);
        _runReader.GetRunAsync(selectedRunId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AgentRunRecord?>(selectedRun));

        // Feedback service must surface a row keyed by the per-iteration RunId.
        _feedbackService.GetFeedbackForRunAsync(selectedRunId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentRunFeedback>>(
                new[] { MakeFeedback("iteration-2 teaching") }));

        await _indexer.IndexAsync(selectedRunId, _agentId, CancellationToken.None);

        // Digest joins the SELECTED iteration's response + prompt (AR35 segment
        // order: Comment → Response → Prompt). If the indexer accidentally
        // resolved via GetRunsForThreadAsync first, the embed input would carry
        // the default `[assistant] hi` body, not "iteration-2 response body".
        await _embeddingService.Received(1).GenerateEmbeddingAsync(
            Arg.Any<Action<AIEmbeddingBuilder>>(),
            "iteration-2 teaching\n\n[assistant] iteration-2 response body\n\n[user] iteration-2 prompt body",
            Arg.Any<CancellationToken>());

        // GetRunsForThreadAsync MUST NOT be called when GetRunAsync resolves —
        // pins the selectedRunId-first contract.
        await _runReader.DidNotReceive().GetRunsForThreadAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task IndexAsync_LegacyThreadIdRow_FallsBackToGetRunsForThreadAsync()
    {
        // Legacy/non-picker path — AgentFeedbackController passed the ThreadId
        // (pre-Story-4.12 row OR a non-picker submission). GetRunAsync(threadId)
        // returns null (no audit-row's per-iteration RunId equals the
        // ThreadId); the indexer falls back to GetRunsForThreadAsync + runs[0]
        // for byte-compatible behaviour.
        const string threadId = "legacy-thread-A";
        _runReader.GetRunAsync(threadId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AgentRunRecord?>(null));
        // GetRunsForThreadAsync returns DESC — runs[0] is most-recent.
        var threadRuns = new[] { MakeRun(runId: threadId) };
        _runReader.GetRunsForThreadAsync(threadId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentRunRecord>>(threadRuns));
        _feedbackService.GetFeedbackForRunAsync(threadId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentRunFeedback>>(
                new[] { MakeFeedback("legacy comment") }));

        await _indexer.IndexAsync(threadId, _agentId, CancellationToken.None);

        // Both reader methods called, in order: GetRunAsync first (returns
        // null), then GetRunsForThreadAsync as fallback.
        Received.InOrder(() =>
        {
            _runReader.GetRunAsync(threadId, Arg.Any<CancellationToken>());
            _runReader.GetRunsForThreadAsync(threadId, Arg.Any<CancellationToken>());
        });

        // Digest reaches the embed call — pins the fallback path doesn't
        // silently skip the row.
        await _embeddingService.Received(1).GenerateEmbeddingAsync(
            Arg.Any<Action<AIEmbeddingBuilder>>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    private sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
        where T : class
    {
        private readonly T _value;
        public TestOptionsMonitor(T value) => _value = value;
        public T CurrentValue => _value;
        public T Get(string? name) => _value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
