using Umbraco.Community.AiAgentMemory.Configuration;
using Umbraco.Community.AiAgentMemory.Feedback;
using Umbraco.Community.AiAgentMemory.Memory;
using Umbraco.Community.AiAgentMemory.Persistence.Entities;
using Umbraco.Community.AiAgentMemory.Persistence.Repositories;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Umbraco.AI.Core.Embeddings;
using Umbraco.AI.Core.Models;
using Umbraco.AI.Core.Profiles;
using Umbraco.AI.Search.Core.VectorStore;

namespace Umbraco.Community.AiAgentMemory.Tests.Memory;

/// <summary>
/// Unit tests for <see cref="SemanticMemoryRetriever"/> (Story 3.2 AC8). Tests
/// pin: happy path; empty-result paths; topK clamp / non-positive / null /
/// empty inputs; per-filter discrimination (agent / workspace / null-workspace
/// tolerance / age / threshold); NFR-R1 silent no-op paths; NFR-R3 graceful
/// degradation (embed / search throws); OperationCanceledException propagation;
/// defensive guards (orphan vector + empty feedback); mixed-cancellation-and-
/// error parallel-hydration cancellation precedence per HIGH review-finding
/// 2026-05-14. NSubstitute stubs wired through a real ServiceCollection so the
/// retriever's per-call IServiceScopeFactory.CreateScope() chain resolves them.
/// Mirrors Story 3.1 FeedbackIndexerTests pattern verbatim.
/// </summary>
[TestFixture]
public class SemanticMemoryRetrieverTests
{
    private static readonly DateTime FixedUtcNow = new(2026, 5, 14, 12, 0, 0, DateTimeKind.Utc);
    private const string DefaultAlias = "openai-embedding";
    private const string DefaultRunId = "run-1";

    private IAIVectorStore _vectorStore = null!;
    private IAIEmbeddingService _embeddingService = null!;
    private IAIProfileService _profileService = null!;
    private IMemoryEntryRepository _repository = null!;
    private IAgentFeedbackService _feedbackService = null!;
    private AgentMemoryOptions _options = null!;
    private AIOptions _aiOptions = null!;
    private FakeTimeProvider _timeProvider = null!;
    private Guid _agentId;
    private Guid _profileId;
    private SemanticMemoryRetriever _retriever = null!;

    [SetUp]
    public void SetUp()
    {
        _vectorStore = Substitute.For<IAIVectorStore>();
        _embeddingService = Substitute.For<IAIEmbeddingService>();
        _profileService = Substitute.For<IAIProfileService>();
        _repository = Substitute.For<IMemoryEntryRepository>();
        _feedbackService = Substitute.For<IAgentFeedbackService>();
        _timeProvider = new FakeTimeProvider(new DateTimeOffset(FixedUtcNow, TimeSpan.Zero));
        _agentId = Guid.NewGuid();
        _profileId = Guid.NewGuid();

        _options = new AgentMemoryOptions
        {
            EmbeddingProfileAlias = DefaultAlias,
            EligibilityThreshold = 0.7,
            MaxMemoryAgeDays = 90,
            TopKMemories = 5,
        };
        _aiOptions = new AIOptions();

        // Default: profile resolves cleanly.
        var profile = MakeProfile(_profileId, DefaultAlias);
        _profileService.GetProfileByAliasAsync(DefaultAlias, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AIProfile?>(profile));

        // Default embedding result — a small in-process vector.
        _embeddingService.GenerateEmbeddingAsync(
                Arg.Any<Action<AIEmbeddingBuilder>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new Embedding<float>(new float[] { 0.1f, 0.2f, 0.3f })));

        // Default search result — empty (each test overrides as needed).
        _vectorStore.SearchAsync(
                Arg.Any<string>(),
                Arg.Any<ReadOnlyMemory<float>>(),
                Arg.Any<string?>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AIVectorSearchResult>>(Array.Empty<AIVectorSearchResult>()));

        // Default repository + feedback — return null + empty respectively.
        _repository.FindByRunIdAndAgentIdAsync(
                Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<MemoryEntryEntity?>(null));
        _feedbackService.GetFeedbackForRunAsync(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentRunFeedback>>(Array.Empty<AgentRunFeedback>()));

        _retriever = BuildRetriever();
    }

    private SemanticMemoryRetriever BuildRetriever()
    {
        // Wire a real ServiceProvider so the retriever's per-call scope can
        // resolve all dependencies via GetRequiredService. Mirrors the Story 3.1
        // FeedbackIndexerTests.BuildIndexer pattern verbatim.
        var services = new ServiceCollection();
        services.AddSingleton(_vectorStore);
        services.AddSingleton(_embeddingService);
        services.AddSingleton(_profileService);
        services.AddSingleton(_repository);
        services.AddSingleton(_feedbackService);
        var provider = services.BuildServiceProvider();

        return new SemanticMemoryRetriever(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new TestOptionsMonitor<AgentMemoryOptions>(_options),
            Options.Create(_aiOptions),
            _timeProvider,
            NullLogger<SemanticMemoryRetriever>.Instance);
    }

    private static AIProfile MakeProfile(Guid id, string alias)
    {
        var profile = new AIProfile
        {
            Alias = alias,
            Name = "test-profile",
            ConnectionId = Guid.NewGuid(),
            Capability = AICapability.Embedding,
        };
        typeof(AIProfile).GetProperty(nameof(AIProfile.Id))!.SetValue(profile, id);
        return profile;
    }

    private AIVectorSearchResult MakeResult(string runId, double score, Guid? agentId = null) =>
        new(
            DocumentId: $"{runId}:{(agentId ?? _agentId):N}",
            Score: score,
            Metadata: new Dictionary<string, object>
            {
                ["agentId"] = (agentId ?? _agentId).ToString("D"),
                ["runId"] = runId,
            });

    private MemoryEntryEntity MakeEntry(
        string runId,
        Guid? workspaceId = null,
        DateTime? embeddedUtc = null,
        Guid? agentId = null,
        string digest = "digest text") => new()
    {
        Id = Guid.NewGuid(),
        RunId = runId,
        AgentId = agentId ?? _agentId,
        WorkspaceId = workspaceId,
        DigestText = digest,
        EmbeddingRef = $"{runId}:{(agentId ?? _agentId):N}",
        IndexingStatus = (int)IndexingStatus.Embedded,
        IndexingError = null,
        EmbeddedUtc = embeddedUtc ?? FixedUtcNow.AddDays(-1),
        CreatedUtc = embeddedUtc ?? FixedUtcNow.AddDays(-1),
    };

    private AgentRunFeedback MakeFeedback(string runId, FeedbackScore score, string? comment) => new(
        Id: Guid.NewGuid(),
        RunId: runId,
        AgentId: _agentId,
        Score: score,
        Comment: comment,
        CreatedBy: Guid.NewGuid(),
        CreatedUtc: FixedUtcNow);

    private static List<ChatMessage> Messages(string text) => new()
    {
        new ChatMessage(ChatRole.User, text),
    };

    // ─────────────────────────────────────────────────────────────────────
    // AC8.1 — happy path
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public async Task RetrieveSimilarAsync_HappyPath_EmbedsQuery_SearchesUnderAlias_FiltersAndJoinsFeedback_ReturnsTopKDescendingBySimilarity()
    {
        var run1 = "run-a"; var run2 = "run-b"; var run3 = "run-c";
        _vectorStore.SearchAsync(
                Arg.Any<string>(), Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<string?>(),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AIVectorSearchResult>>(new[]
            {
                MakeResult(run1, 0.85),
                MakeResult(run2, 0.78),
                MakeResult(run3, 0.72),
            }));
        _repository.FindByRunIdAndAgentIdAsync(run1, _agentId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<MemoryEntryEntity?>(MakeEntry(run1, digest: "alpha digest")));
        _repository.FindByRunIdAndAgentIdAsync(run2, _agentId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<MemoryEntryEntity?>(MakeEntry(run2, digest: "beta digest")));
        _repository.FindByRunIdAndAgentIdAsync(run3, _agentId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<MemoryEntryEntity?>(MakeEntry(run3, digest: "gamma digest")));
        _feedbackService.GetFeedbackForRunAsync(run1, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentRunFeedback>>(new[] { MakeFeedback(run1, FeedbackScore.ThumbsUp, "alpha comment") }));
        _feedbackService.GetFeedbackForRunAsync(run2, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentRunFeedback>>(new[] { MakeFeedback(run2, FeedbackScore.ThumbsDown, "beta comment") }));
        _feedbackService.GetFeedbackForRunAsync(run3, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentRunFeedback>>(new[] { MakeFeedback(run3, FeedbackScore.Neutral, "gamma comment") }));

        var result = await _retriever.RetrieveSimilarAsync(_agentId, null, Messages("query"), 5, CancellationToken.None);

        Assert.That(result, Has.Count.EqualTo(3));
        Assert.That(result[0].RunId, Is.EqualTo(run1));
        Assert.That(result[0].Summary, Is.EqualTo("alpha digest"));
        Assert.That(result[0].Score, Is.EqualTo(FeedbackScore.ThumbsUp));
        Assert.That(result[0].FeedbackComment, Is.EqualTo("alpha comment"));
        Assert.That(result[0].SimilarityScore, Is.EqualTo(0.85));
        Assert.That(result[1].SimilarityScore, Is.GreaterThan(result[2].SimilarityScore),
            "ordering must be SimilarityScore DESC");

        // Embedding called once with the WithAlias triplet + the query text.
        await _embeddingService.Received(1).GenerateEmbeddingAsync(
            Arg.Any<Action<AIEmbeddingBuilder>>(),
            "query",
            Arg.Any<CancellationToken>());

        // SearchAsync called with the right index alias + culture null + topK exact.
        await _vectorStore.Received(1).SearchAsync(
            "cogworks-agent-memory",
            Arg.Any<ReadOnlyMemory<float>>(),
            Arg.Is<string?>(c => c == null),
            5,
            Arg.Any<CancellationToken>());
    }

    // ─────────────────────────────────────────────────────────────────────
    // AC8.2 — vector store returns empty
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public async Task RetrieveSimilarAsync_VectorStoreReturnsEmpty_ReturnsEmptyList_NoRepositoryOrFeedbackCalls()
    {
        // Default SetUp returns empty vector-store result.
        var result = await _retriever.RetrieveSimilarAsync(_agentId, null, Messages("query"), 5, CancellationToken.None);

        Assert.That(result, Is.Empty);
        await _repository.DidNotReceiveWithAnyArgs().FindByRunIdAndAgentIdAsync(default!, default, default);
        await _feedbackService.DidNotReceiveWithAnyArgs().GetFeedbackForRunAsync(default!, default);
    }

    // ─────────────────────────────────────────────────────────────────────
    // AC8.3 — all candidates fail filters
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public async Task RetrieveSimilarAsync_AllCandidatesFailThresholdFilter_ReturnsEmptyList()
    {
        // 2 candidates, both BELOW threshold 0.7.
        _vectorStore.SearchAsync(
                Arg.Any<string>(), Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<string?>(),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AIVectorSearchResult>>(new[]
            {
                MakeResult("run-a", 0.55),
                MakeResult("run-b", 0.30),
            }));

        var result = await _retriever.RetrieveSimilarAsync(_agentId, null, Messages("query"), 5, CancellationToken.None);

        Assert.That(result, Is.Empty);
        await _repository.DidNotReceiveWithAnyArgs().FindByRunIdAndAgentIdAsync(default!, default, default);
    }

    // ─────────────────────────────────────────────────────────────────────
    // AC8.4 — topK clamps to 10
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public async Task RetrieveSimilarAsync_TopKAboveTen_ClampsToTen()
    {
        await _retriever.RetrieveSimilarAsync(_agentId, null, Messages("query"), 99, CancellationToken.None);

        await _vectorStore.Received(1).SearchAsync(
            Arg.Any<string>(),
            Arg.Any<ReadOnlyMemory<float>>(),
            Arg.Any<string?>(),
            10,
            Arg.Any<CancellationToken>());
    }

    // ─────────────────────────────────────────────────────────────────────
    // AC8.5 — agentId filter
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public async Task RetrieveSimilarAsync_AgentIdFilter_OnlyAgentMatchesReturned()
    {
        var otherAgent = Guid.NewGuid();
        var run1 = "run-mine"; var run2 = "run-other";
        _vectorStore.SearchAsync(
                Arg.Any<string>(), Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<string?>(),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AIVectorSearchResult>>(new[]
            {
                MakeResult(run1, 0.85),
                MakeResult(run2, 0.85, agentId: otherAgent),
            }));
        _repository.FindByRunIdAndAgentIdAsync(run1, _agentId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<MemoryEntryEntity?>(MakeEntry(run1)));

        var result = await _retriever.RetrieveSimilarAsync(_agentId, null, Messages("query"), 5, CancellationToken.None);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].RunId, Is.EqualTo(run1));
    }

    // ─────────────────────────────────────────────────────────────────────
    // AC8.6 — LOAD-BEARING cross-workspace isolation (FR35 / NFR-S4)
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public async Task RetrieveSimilarAsync_CrossWorkspaceMemories_NeverLeak()
    {
        var w1 = Guid.NewGuid();
        var w2 = Guid.NewGuid();
        var runW1A = "run-w1-a"; var runW1B = "run-w1-b";
        var runW2A = "run-w2-a"; var runW2B = "run-w2-b";

        _vectorStore.SearchAsync(
                Arg.Any<string>(), Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<string?>(),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AIVectorSearchResult>>(new[]
            {
                MakeResult(runW1A, 0.90),
                MakeResult(runW1B, 0.85),
                MakeResult(runW2A, 0.95),
                MakeResult(runW2B, 0.80),
            }));
        _repository.FindByRunIdAndAgentIdAsync(runW1A, _agentId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<MemoryEntryEntity?>(MakeEntry(runW1A, workspaceId: w1)));
        _repository.FindByRunIdAndAgentIdAsync(runW1B, _agentId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<MemoryEntryEntity?>(MakeEntry(runW1B, workspaceId: w1)));
        _repository.FindByRunIdAndAgentIdAsync(runW2A, _agentId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<MemoryEntryEntity?>(MakeEntry(runW2A, workspaceId: w2)));
        _repository.FindByRunIdAndAgentIdAsync(runW2B, _agentId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<MemoryEntryEntity?>(MakeEntry(runW2B, workspaceId: w2)));

        var result = await _retriever.RetrieveSimilarAsync(_agentId, w1, Messages("query"), 5, CancellationToken.None);

        Assert.That(result, Has.Count.EqualTo(2),
            "exactly 2 W1 memories must surface; ZERO W2 leakage (FR35 / NFR-S4 load-bearing security pin)");
        Assert.That(result.Select(m => m.RunId), Is.EquivalentTo(new[] { runW1A, runW1B }));
        Assert.That(result.Select(m => m.RunId), Does.Not.Contain(runW2A));
        Assert.That(result.Select(m => m.RunId), Does.Not.Contain(runW2B));
    }

    // ─────────────────────────────────────────────────────────────────────
    // AC8.7 — workspaceId null tolerance (FR36) + null-doesn't-leak-to-W1
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public async Task RetrieveSimilarAsync_WorkspaceIdNull_TolerantToNullEntries()
    {
        var run1 = "run-null-1"; var run2 = "run-null-2"; var run3 = "run-null-3";
        _vectorStore.SearchAsync(
                Arg.Any<string>(), Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<string?>(),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AIVectorSearchResult>>(new[]
            {
                MakeResult(run1, 0.85),
                MakeResult(run2, 0.80),
                MakeResult(run3, 0.75),
            }));
        _repository.FindByRunIdAndAgentIdAsync(run1, _agentId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<MemoryEntryEntity?>(MakeEntry(run1, workspaceId: null)));
        _repository.FindByRunIdAndAgentIdAsync(run2, _agentId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<MemoryEntryEntity?>(MakeEntry(run2, workspaceId: null)));
        _repository.FindByRunIdAndAgentIdAsync(run3, _agentId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<MemoryEntryEntity?>(MakeEntry(run3, workspaceId: null)));

        var result = await _retriever.RetrieveSimilarAsync(_agentId, null, Messages("query"), 5, CancellationToken.None);

        Assert.That(result, Has.Count.EqualTo(3),
            "FR36 null-tolerance — workspaceId=null caller tolerates all null entries");
    }

    [Test]
    public async Task RetrieveSimilarAsync_WorkspaceIdNonNull_NullEntriesDoNotLeak()
    {
        // FR35 asymmetry pin: null-workspace caller tolerates null-workspace
        // entries (FR36); but a non-null caller MUST NOT accept null-workspace
        // entries as a "match" — that would be cross-workspace null-leak.
        var w1 = Guid.NewGuid();
        var run1 = "run-nullws";
        _vectorStore.SearchAsync(
                Arg.Any<string>(), Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<string?>(),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AIVectorSearchResult>>(new[] { MakeResult(run1, 0.95) }));
        _repository.FindByRunIdAndAgentIdAsync(run1, _agentId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<MemoryEntryEntity?>(MakeEntry(run1, workspaceId: null)));

        var result = await _retriever.RetrieveSimilarAsync(_agentId, w1, Messages("query"), 5, CancellationToken.None);

        Assert.That(result, Is.Empty,
            "FR35: a workspaceId=W caller must NOT receive null-workspace entries (no cross-workspace null-leak)");
    }

    // ─────────────────────────────────────────────────────────────────────
    // AC8.8 — age filter (FR23)
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public async Task RetrieveSimilarAsync_AgeFilter_EntriesOlderThanMaxAge_Excluded()
    {
        // MaxMemoryAgeDays = 90 (default in SetUp).
        var runRecent = "run-recent"; var runOld = "run-old";
        _vectorStore.SearchAsync(
                Arg.Any<string>(), Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<string?>(),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AIVectorSearchResult>>(new[]
            {
                MakeResult(runRecent, 0.85),
                MakeResult(runOld, 0.80),
            }));
        _repository.FindByRunIdAndAgentIdAsync(runRecent, _agentId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<MemoryEntryEntity?>(MakeEntry(runRecent, embeddedUtc: FixedUtcNow.AddDays(-30))));
        _repository.FindByRunIdAndAgentIdAsync(runOld, _agentId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<MemoryEntryEntity?>(MakeEntry(runOld, embeddedUtc: FixedUtcNow.AddDays(-120))));

        var result = await _retriever.RetrieveSimilarAsync(_agentId, null, Messages("query"), 5, CancellationToken.None);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].RunId, Is.EqualTo(runRecent));
    }

    // ─────────────────────────────────────────────────────────────────────
    // AC8.9 — threshold filter (FR24)
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public async Task RetrieveSimilarAsync_ThresholdFilter_BelowEligibilityThreshold_Excluded()
    {
        // 4 candidates: 2 above 0.7 threshold, 2 below.
        var hi1 = "run-hi1"; var hi2 = "run-hi2"; var lo1 = "run-lo1"; var lo2 = "run-lo2";
        _vectorStore.SearchAsync(
                Arg.Any<string>(), Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<string?>(),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AIVectorSearchResult>>(new[]
            {
                MakeResult(hi1, 0.85),
                MakeResult(hi2, 0.72),
                MakeResult(lo1, 0.55),
                MakeResult(lo2, 0.30),
            }));
        _repository.FindByRunIdAndAgentIdAsync(hi1, _agentId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<MemoryEntryEntity?>(MakeEntry(hi1)));
        _repository.FindByRunIdAndAgentIdAsync(hi2, _agentId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<MemoryEntryEntity?>(MakeEntry(hi2)));

        var result = await _retriever.RetrieveSimilarAsync(_agentId, null, Messages("query"), 5, CancellationToken.None);

        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result.Select(m => m.RunId), Is.EquivalentTo(new[] { hi1, hi2 }));
    }

    // ─────────────────────────────────────────────────────────────────────
    // AC8.10 — topK <= 0 short-circuit
    // ─────────────────────────────────────────────────────────────────────

    [TestCase(0)]
    [TestCase(-1)]
    [TestCase(int.MinValue)]
    public async Task RetrieveSimilarAsync_TopKZeroOrNegative_ReturnsEmptyList(int topK)
    {
        var result = await _retriever.RetrieveSimilarAsync(_agentId, null, Messages("query"), topK, CancellationToken.None);

        Assert.That(result, Is.Empty);
        await _embeddingService.DidNotReceiveWithAnyArgs().GenerateEmbeddingAsync(
            default(Action<AIEmbeddingBuilder>)!, default!, default);
        await _vectorStore.DidNotReceiveWithAnyArgs().SearchAsync(default!, default, default, default, default);
    }

    // ─────────────────────────────────────────────────────────────────────
    // AC8.11 — empty / whitespace messages
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public async Task RetrieveSimilarAsync_EmptyCurrentMessages_ReturnsEmptyList()
    {
        var result = await _retriever.RetrieveSimilarAsync(
            _agentId, null, new List<ChatMessage>(), 5, CancellationToken.None);

        Assert.That(result, Is.Empty);
        await _embeddingService.DidNotReceiveWithAnyArgs().GenerateEmbeddingAsync(
            default(Action<AIEmbeddingBuilder>)!, default!, default);
    }

    [Test]
    public async Task RetrieveSimilarAsync_AllWhitespaceMessages_ReturnsEmptyList()
    {
        var result = await _retriever.RetrieveSimilarAsync(
            _agentId, null, new List<ChatMessage> { new(ChatRole.User, "   "), new(ChatRole.User, "\n\t") },
            5, CancellationToken.None);

        Assert.That(result, Is.Empty);
        await _embeddingService.DidNotReceiveWithAnyArgs().GenerateEmbeddingAsync(
            default(Action<AIEmbeddingBuilder>)!, default!, default);
    }

    // ─────────────────────────────────────────────────────────────────────
    // AC8.12 — null currentMessages throws ArgumentNullException
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public void RetrieveSimilarAsync_NullCurrentMessages_ThrowsArgumentNullException()
    {
        var ex = Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await _retriever.RetrieveSimilarAsync(_agentId, null, null!, 5, CancellationToken.None));
        Assert.That(ex!.ParamName, Is.EqualTo("currentMessages"));
    }

    // ─────────────────────────────────────────────────────────────────────
    // AC8.13 — no embedding profile alias configured (NFR-R1 silent no-op)
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public async Task RetrieveSimilarAsync_NoEmbeddingProfileAliasConfigured_SilentlyNoOps()
    {
        _options.EmbeddingProfileAlias = null;
        _aiOptions.DefaultEmbeddingProfileAlias = null;

        var result = await _retriever.RetrieveSimilarAsync(_agentId, null, Messages("query"), 5, CancellationToken.None);

        Assert.That(result, Is.Empty);
        await _profileService.DidNotReceiveWithAnyArgs().GetProfileByAliasAsync(default!, default);
        await _embeddingService.DidNotReceiveWithAnyArgs().GenerateEmbeddingAsync(
            default(Action<AIEmbeddingBuilder>)!, default!, default);
        await _vectorStore.DidNotReceiveWithAnyArgs().SearchAsync(default!, default, default, default, default);
    }

    // ─────────────────────────────────────────────────────────────────────
    // AC8.14 — profile lookup returns null (NFR-R1 silent no-op)
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public async Task RetrieveSimilarAsync_EmbeddingProfileAliasLookupReturnsNull_SilentlyNoOps()
    {
        _profileService.GetProfileByAliasAsync(DefaultAlias, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AIProfile?>(null));

        var result = await _retriever.RetrieveSimilarAsync(_agentId, null, Messages("query"), 5, CancellationToken.None);

        Assert.That(result, Is.Empty);
        await _embeddingService.DidNotReceiveWithAnyArgs().GenerateEmbeddingAsync(
            default(Action<AIEmbeddingBuilder>)!, default!, default);
        await _vectorStore.DidNotReceiveWithAnyArgs().SearchAsync(default!, default, default, default, default);
    }

    // ─────────────────────────────────────────────────────────────────────
    // AC8.15 — embedding service throws (NFR-R3 graceful degradation)
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public async Task RetrieveSimilarAsync_EmbeddingServiceThrows_LogsWarningAndReturnsEmptyList_NoRetry()
    {
        _embeddingService.GenerateEmbeddingAsync(
                Arg.Any<Action<AIEmbeddingBuilder>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<Embedding<float>>>(_ => throw new HttpRequestException("503 transient"));

        var result = await _retriever.RetrieveSimilarAsync(_agentId, null, Messages("query"), 5, CancellationToken.None);

        Assert.That(result, Is.Empty);
        // Exactly one call — no retry on the retrieval hot path.
        await _embeddingService.Received(1).GenerateEmbeddingAsync(
            Arg.Any<Action<AIEmbeddingBuilder>>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
        await _vectorStore.DidNotReceiveWithAnyArgs().SearchAsync(default!, default, default, default, default);
    }

    // ─────────────────────────────────────────────────────────────────────
    // AC8.16 — vector store throws (NFR-R3 graceful degradation)
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public async Task RetrieveSimilarAsync_VectorStoreThrows_LogsWarningAndReturnsEmptyList_NoRetry()
    {
        _vectorStore.SearchAsync(
                Arg.Any<string>(), Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<string?>(),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<AIVectorSearchResult>>>(_ => throw new InvalidOperationException("upstream error"));

        var result = await _retriever.RetrieveSimilarAsync(_agentId, null, Messages("query"), 5, CancellationToken.None);

        Assert.That(result, Is.Empty);
        await _repository.DidNotReceiveWithAnyArgs().FindByRunIdAndAgentIdAsync(default!, default, default);
        await _feedbackService.DidNotReceiveWithAnyArgs().GetFeedbackForRunAsync(default!, default);
    }

    // ─────────────────────────────────────────────────────────────────────
    // AC8.17 — OperationCanceledException propagates unwrapped
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public void RetrieveSimilarAsync_OperationCanceledException_PropagatesUnwrapped_NoRetry()
    {
        _embeddingService.GenerateEmbeddingAsync(
                Arg.Any<Action<AIEmbeddingBuilder>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<Embedding<float>>>(_ => throw new OperationCanceledException());

        Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await _retriever.RetrieveSimilarAsync(_agentId, null, Messages("query"), 5, CancellationToken.None));
    }

    // ─────────────────────────────────────────────────────────────────────
    // AC8.18 — orphan vector (entries row missing) silently skipped
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public async Task RetrieveSimilarAsync_EntriesRowMissingForCandidate_SkipsThatCandidate()
    {
        var run1 = "run-ok"; var orphan = "run-orphan"; var run3 = "run-ok-2";
        _vectorStore.SearchAsync(
                Arg.Any<string>(), Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<string?>(),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AIVectorSearchResult>>(new[]
            {
                MakeResult(run1, 0.85),
                MakeResult(orphan, 0.83),
                MakeResult(run3, 0.80),
            }));
        _repository.FindByRunIdAndAgentIdAsync(run1, _agentId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<MemoryEntryEntity?>(MakeEntry(run1)));
        _repository.FindByRunIdAndAgentIdAsync(orphan, _agentId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<MemoryEntryEntity?>(null));
        _repository.FindByRunIdAndAgentIdAsync(run3, _agentId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<MemoryEntryEntity?>(MakeEntry(run3)));

        var result = await _retriever.RetrieveSimilarAsync(_agentId, null, Messages("query"), 5, CancellationToken.None);

        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result.Select(m => m.RunId), Is.EquivalentTo(new[] { run1, run3 }));
        Assert.That(result.Select(m => m.RunId), Does.Not.Contain(orphan));
    }

    // ─────────────────────────────────────────────────────────────────────
    // AC8.19 — empty feedback list still returns MemoryEntry with null Score
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public async Task RetrieveSimilarAsync_FeedbackEmptyForCandidate_StillReturnsMemoryEntryWithNullScoreAndComment()
    {
        var run1 = "run-no-feedback";
        _vectorStore.SearchAsync(
                Arg.Any<string>(), Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<string?>(),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AIVectorSearchResult>>(new[] { MakeResult(run1, 0.85) }));
        _repository.FindByRunIdAndAgentIdAsync(run1, _agentId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<MemoryEntryEntity?>(MakeEntry(run1)));
        _feedbackService.GetFeedbackForRunAsync(run1, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentRunFeedback>>(Array.Empty<AgentRunFeedback>()));

        var result = await _retriever.RetrieveSimilarAsync(_agentId, null, Messages("query"), 5, CancellationToken.None);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].RunId, Is.EqualTo(run1));
        Assert.That(result[0].Score, Is.Null);
        Assert.That(result[0].FeedbackComment, Is.Null);
        Assert.That(result[0].Summary, Is.EqualTo("digest text"),
            "the entries-row digest is the load-bearing signal; feedback is auxiliary");
    }

    // ─────────────────────────────────────────────────────────────────────
    // AC8.20 — parallel hydration mixed cancellation + error → OCE wins
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public void RetrieveSimilarAsync_ParallelHydration_MixedCancellationAndError_PropagatesCancellation()
    {
        var runOk = "run-ok"; var runErr = "run-err"; var runCancel = "run-cancel";
        _vectorStore.SearchAsync(
                Arg.Any<string>(), Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<string?>(),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AIVectorSearchResult>>(new[]
            {
                MakeResult(runOk, 0.85),
                MakeResult(runErr, 0.82),
                MakeResult(runCancel, 0.80),
            }));
        _repository.FindByRunIdAndAgentIdAsync(runOk, _agentId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<MemoryEntryEntity?>(MakeEntry(runOk)));
        _repository.FindByRunIdAndAgentIdAsync(runErr, _agentId, Arg.Any<CancellationToken>())
            .Returns<Task<MemoryEntryEntity?>>(_ => Task.FromException<MemoryEntryEntity?>(new HttpRequestException("transient")));
        _repository.FindByRunIdAndAgentIdAsync(runCancel, _agentId, Arg.Any<CancellationToken>())
            .Returns<Task<MemoryEntryEntity?>>(_ => Task.FromException<MemoryEntryEntity?>(new OperationCanceledException()));

        // Cancellation always wins per § Locked decision #15 — even though only
        // one of the 3 parallel tasks faulted with OCE, the retriever propagates
        // the cancellation rather than the Warning + empty list path.
        Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await _retriever.RetrieveSimilarAsync(_agentId, null, Messages("query"), 5, CancellationToken.None));
    }

    // ─────────────────────────────────────────────────────────────────────
    // AC8.20b — parallel hydration mixed REAL (IsCanceled) cancellation + error
    //           — code-review 2026-05-14 HIGH counterpart to AC8.20.
    //
    // AC8.20 exercises the Task.FromException(new OCE()) shape (IsFaulted=true
    // with OCE in InnerExceptions). Real token-driven cancellation lands a
    // task as IsCanceled=true (no exception in InnerExceptions). The original
    // AnyFaultIsCancellation helper only walked IsFaulted tasks, silently
    // swallowing real cancellation when Task.WhenAll rethrew the non-OCE
    // faulted exception first. This test pins the fixed
    // AnyTaskObservedCancellation helper's IsCanceled branch.
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public void RetrieveSimilarAsync_ParallelHydration_MixedRealCancellationAndError_PropagatesCancellation()
    {
        var runOk = "run-ok-real"; var runErr = "run-err-real"; var runCancel = "run-cancel-real";
        _vectorStore.SearchAsync(
                Arg.Any<string>(), Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<string?>(),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AIVectorSearchResult>>(new[]
            {
                MakeResult(runOk, 0.85),
                MakeResult(runErr, 0.82),
                MakeResult(runCancel, 0.80),
            }));
        _repository.FindByRunIdAndAgentIdAsync(runOk, _agentId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<MemoryEntryEntity?>(MakeEntry(runOk)));
        _repository.FindByRunIdAndAgentIdAsync(runErr, _agentId, Arg.Any<CancellationToken>())
            .Returns<Task<MemoryEntryEntity?>>(_ => Task.FromException<MemoryEntryEntity?>(new HttpRequestException("transient")));
        // Real cancellation: Task.FromCanceled lands the task as IsCanceled=true
        // (no exception inside InnerExceptions) — the production-realistic shape
        // that the original helper missed.
        using var alreadyCanceled = new CancellationTokenSource();
        alreadyCanceled.Cancel();
        var realCanceledTask = Task.FromCanceled<MemoryEntryEntity?>(alreadyCanceled.Token);
        _repository.FindByRunIdAndAgentIdAsync(runCancel, _agentId, Arg.Any<CancellationToken>())
            .Returns<Task<MemoryEntryEntity?>>(_ => realCanceledTask);

        // Even though Task.WhenAll rethrows the non-OCE faulted exception first
        // (faulted-rethrow precedence over canceled), AnyTaskObservedCancellation
        // must detect the IsCanceled task and surface OperationCanceledException.
        Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await _retriever.RetrieveSimilarAsync(_agentId, null, Messages("query"), 5, CancellationToken.None));
    }

    // ─────────────────────────────────────────────────────────────────────
    // AC8.21 — NaN similarity score rejected by pre-filter
    //          — code-review 2026-05-14 MEDIUM IEEE-754 finding.
    //
    // IEEE-754: NaN < threshold is always false. Without explicit rejection,
    // a NaN-scored candidate would survive the threshold pre-filter, hydrate,
    // project, and surface to the middleware with SimilarityScore = NaN.
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public async Task RetrieveSimilarAsync_VectorStoreReturnsNaNScore_CandidateRejected()
    {
        var runOk = "run-ok"; var runNaN = "run-nan";
        _vectorStore.SearchAsync(
                Arg.Any<string>(), Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<string?>(),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AIVectorSearchResult>>(new[]
            {
                MakeResult(runOk, 0.85),
                MakeResult(runNaN, double.NaN),
            }));
        _repository.FindByRunIdAndAgentIdAsync(runOk, _agentId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<MemoryEntryEntity?>(MakeEntry(runOk)));

        var result = await _retriever.RetrieveSimilarAsync(_agentId, null, Messages("query"), 5, CancellationToken.None);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].RunId, Is.EqualTo(runOk));
        Assert.That(result.Select(m => m.RunId), Does.Not.Contain(runNaN));
        await _repository.DidNotReceive().FindByRunIdAndAgentIdAsync(runNaN, _agentId, Arg.Any<CancellationToken>());
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
