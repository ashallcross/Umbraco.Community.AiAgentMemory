using Cogworks.UmbracoAI.AgentMemory.Configuration;
using Cogworks.UmbracoAI.AgentMemory.Feedback;
using Cogworks.UmbracoAI.AgentMemory.Persistence.Entities;
using Cogworks.UmbracoAI.AgentMemory.Persistence.Repositories;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.AI.Core.Embeddings;
using Umbraco.AI.Core.Models;
using Umbraco.AI.Core.Profiles;
using Umbraco.AI.Search.Core.VectorStore;

namespace Cogworks.UmbracoAI.AgentMemory.Memory;

/// <summary>
/// Semantic memory retriever — embeds the current chat input via Umbraco.AI's
/// <see cref="IAIEmbeddingService"/> builder API (alias
/// <c>cogworks-agent-memory-retriever</c>), queries
/// <see cref="IAIVectorStore.SearchAsync"/> under index alias
/// <c>cogworks-agent-memory</c>, applies the four post-fetch filters in C#
/// (<c>agentId</c> + <c>workspaceId</c> + age + cosine threshold per FR21 / FR22 /
/// FR23 / FR24 / FR35 / FR36 / NFR-S4), hydrates entries rows via
/// <see cref="IMemoryEntryRepository.FindByRunIdAndAgentIdAsync"/>, JOINs the
/// most-recent feedback row per <see cref="IAgentFeedbackService.GetFeedbackForRunAsync"/>,
/// and returns up to <c>topK</c> <see cref="MemoryEntry"/> records ordered by
/// similarity descending. Singleton lifetime; constructor takes only framework
/// + package Singletons; all upstream / Scoped deps resolved per-call via
/// <see cref="IServiceScopeFactory.CreateScope"/> (mirrors Story 3.1
/// <see cref="FeedbackIndexer"/> pattern verbatim — captive-dep risk
/// structurally zero).
/// </summary>
internal sealed class SemanticMemoryRetriever : IMemoryRetriever
{
    private const int MaxTopK = 10;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<AgentMemoryOptions> _options;
    private readonly IOptions<AIOptions> _aiOptions;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SemanticMemoryRetriever> _logger;

    public SemanticMemoryRetriever(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<AgentMemoryOptions> options,
        IOptions<AIOptions> aiOptions,
        TimeProvider timeProvider,
        ILogger<SemanticMemoryRetriever> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(aiOptions);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _scopeFactory = scopeFactory;
        _options = options;
        _aiOptions = aiOptions;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<IReadOnlyList<MemoryEntry>> RetrieveSimilarAsync(
        Guid agentId,
        Guid? workspaceId,
        IReadOnlyList<ChatMessage> currentMessages,
        int topK,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(currentMessages);

        if (topK <= 0)
        {
            _logger.LogDebug(
                "SemanticMemoryRetriever — non-positive topK {TopK} for agent {AgentId}; returning empty. Reason={Reason}.",
                topK, agentId, "NonPositiveTopK");
            return Array.Empty<MemoryEntry>();
        }

        var effectiveTopK = Math.Min(topK, MaxTopK);
        if (topK > MaxTopK)
        {
            _logger.LogDebug(
                "SemanticMemoryRetriever — topK {RequestedTopK} clamped to {MaxTopK} for agent {AgentId}. Reason={Reason}.",
                topK, MaxTopK, agentId, "TopKClampedToTen");
        }

        var embedInput = ConcatenateMessages(currentMessages);
        if (string.IsNullOrWhiteSpace(embedInput))
        {
            _logger.LogDebug(
                "SemanticMemoryRetriever — empty or whitespace embedding input for agent {AgentId}; returning empty. Reason={Reason}.",
                agentId, "EmptyOrWhitespaceQueryInput");
            return Array.Empty<MemoryEntry>();
        }

        var options = _options.CurrentValue;

        // Per-call scope. Mirrors Story 3.1 FeedbackIndexer.cs:95-127 verbatim.
        // Captive-dep risk structurally zero — every non-Singleton dep is
        // resolved fresh per call; upstream Umbraco.AI lifetimes (Singleton /
        // Scoped) are irrelevant.
        using var scope = _scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;

        IAIVectorStore vectorStore;
        IAIEmbeddingService embeddingService;
        IAIProfileService profileService;
        IMemoryEntryRepository repository;
        IAgentFeedbackService feedbackService;
        try
        {
            vectorStore = sp.GetRequiredService<IAIVectorStore>();
            embeddingService = sp.GetRequiredService<IAIEmbeddingService>();
            profileService = sp.GetRequiredService<IAIProfileService>();
            repository = sp.GetRequiredService<IMemoryEntryRepository>();
            feedbackService = sp.GetRequiredService<IAgentFeedbackService>();
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex,
                "SemanticMemoryRetriever — upstream service not registered: {Service}. "
                + "Check the Umbraco.AI / Umbraco.AI.Search package graph. "
                + "Agent {AgentId} retrieval returns empty.",
                ex.Message, agentId);
            return Array.Empty<MemoryEntry>();
        }

        // Resolve embedding profile alias (alias fallback chain → null ⇒ NFR-R1 silent no-op).
        var alias = options.EmbeddingProfileAlias ?? _aiOptions.Value.DefaultEmbeddingProfileAlias;
        if (string.IsNullOrEmpty(alias))
        {
            _logger.LogDebug(
                "SemanticMemoryRetriever — silent no-op for agent {AgentId} (workspace {WorkspaceId}); Reason={Reason}.",
                agentId, workspaceId, "NoEmbeddingProfileAliasConfigured");
            return Array.Empty<MemoryEntry>();
        }

        AIProfile? profile;
        try
        {
            profile = await profileService.GetProfileByAliasAsync(alias, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "SemanticMemoryRetriever — profile lookup failed for alias {Alias} (agent {AgentId}); returning empty.",
                alias, agentId);
            return Array.Empty<MemoryEntry>();
        }
        if (profile is null)
        {
            _logger.LogDebug(
                "SemanticMemoryRetriever — silent no-op for agent {AgentId} (workspace {WorkspaceId}); Reason={Reason}.",
                agentId, workspaceId, "EmbeddingProfileAliasLookupReturnedNull");
            return Array.Empty<MemoryEntry>();
        }

        // Embed query via builder API. WithAlias is mandatory per
        // AIEmbeddingBuilder.Validate() (Story 3.1 DRIFT-3.1-impl-1); the call-site
        // alias is a URL-safe observability identifier (NOT the profile alias),
        // pinned to the brand-anchor so adopter audit-log surfaces attribute
        // the row back to the memory pipeline. Sibling to Story 3.1's
        // "cogworks-agent-memory-feedback-indexer" alias.
        Embedding<float> embedding;
        try
        {
            embedding = await embeddingService.GenerateEmbeddingAsync(
                configure => configure
                    .WithAlias("cogworks-agent-memory-retriever")
                    .WithProfile(profile.Id)
                    .WithDescription("Cogworks.UmbracoAI.AgentMemory SemanticMemoryRetriever query"),
                embedInput,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "SemanticMemoryRetriever — embedding call failed for agent {AgentId}; returning empty (NFR-R3 graceful degradation).",
                agentId);
            return Array.Empty<MemoryEntry>();
        }

        // Search vector store. culture: null per Spike 0.B § Spec drift note 1.
        IReadOnlyList<AIVectorSearchResult> results;
        try
        {
            results = await vectorStore.SearchAsync(
                indexName: options.VectorIndex.Alias,
                queryVector: embedding.Vector,
                culture: null,
                topK: effectiveTopK,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "SemanticMemoryRetriever — vector store search failed for agent {AgentId}; returning empty (NFR-R3 graceful degradation).",
                agentId);
            return Array.Empty<MemoryEntry>();
        }

        if (results.Count == 0)
        {
            _logger.LogDebug(
                "SemanticMemoryRetriever — zero candidates returned by vector store for agent {AgentId} (workspace {WorkspaceId}, topK {TopK}). Reason={Reason}.",
                agentId, workspaceId, effectiveTopK, "VectorStoreReturnedZeroCandidates");
            return Array.Empty<MemoryEntry>();
        }

        // Cheap C# pre-filter: agent + threshold + defensive metadata-key check.
        // Defensive against pre-Story-3.1 entries (which shouldn't exist but guard
        // anyway) and against any future provider that emits malformed metadata.
        var threshold = options.EligibilityThreshold;
        var preFiltered = new List<(AIVectorSearchResult Result, string RunId)>(results.Count);
        foreach (var r in results)
        {
            if (!TryExtractMetadata(r.Metadata, r.DocumentId, out var resultAgentId, out var runId))
            {
                continue;
            }
            // IEEE-754: NaN < threshold is always false, so a NaN-scored
            // candidate would otherwise survive the threshold filter, leak
            // through hydration + projection, and surface to the middleware
            // with SimilarityScore = NaN. Reject explicitly. Code-review
            // 2026-05-14 MEDIUM finding.
            if (resultAgentId != agentId || double.IsNaN(r.Score) || r.Score < threshold)
            {
                continue;
            }
            preFiltered.Add((r, runId));
        }

        if (preFiltered.Count == 0)
        {
            _logger.LogDebug(
                "SemanticMemoryRetriever — all candidates filtered out for agent {AgentId} (workspace {WorkspaceId}, topK {TopK}). Reason={Reason}, Cause={Cause}.",
                agentId, workspaceId, effectiveTopK, "AllCandidatesFilteredOut", "AgentOrThreshold");
            return Array.Empty<MemoryEntry>();
        }

        // Parallel entries-row hydration. Task.WhenAll exception handling per
        // § Locked decision #15 — await rethrows ONE exception; inspect
        // tasks' Exception.InnerExceptions to discriminate OCE-in-mixed-faults.
        var entriesTasks = preFiltered
            .Select(p => repository.FindByRunIdAndAgentIdAsync(p.RunId, agentId, cancellationToken))
            .ToArray();
        try
        {
            await Task.WhenAll(entriesTasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            if (AnyTaskObservedCancellation(entriesTasks))
            {
                throw new OperationCanceledException(cancellationToken);
            }
            var firstFault = FirstFaultException(entriesTasks);
            _logger.LogWarning(firstFault,
                "SemanticMemoryRetriever — entries-row hydration faulted for agent {AgentId}; returning empty (NFR-R3 graceful degradation).",
                agentId);
            return Array.Empty<MemoryEntry>();
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var ageCutoff = now - TimeSpan.FromDays(options.MaxMemoryAgeDays);

        // Workspace + age + orphan-vector filter, applied against the entries row.
        var survivors = new List<(AIVectorSearchResult Result, MemoryEntryEntity Entry)>(preFiltered.Count);
        for (var i = 0; i < preFiltered.Count; i++)
        {
            var entry = entriesTasks[i].Result;
            var (vectorResult, runId) = preFiltered[i];

            if (entry is null)
            {
                _logger.LogDebug(
                    "SemanticMemoryRetriever — orphan vector skipped (no entries row) for agent {AgentId}, RunId {RunId}, DocumentId {DocumentId}. Reason={Reason}.",
                    agentId, runId, vectorResult.DocumentId, "OrphanVectorSkipped");
                continue;
            }

            // Failed-status rows have EmbeddedUtc = null; their embedding never landed,
            // so any vector-store row referencing them is orphaned. Excluded by the
            // age filter's null-guard.
            if (entry.EmbeddedUtc is null || entry.EmbeddedUtc < ageCutoff)
            {
                continue;
            }

            // Workspace filter: when caller passes null workspaceId, FR36 tolerates
            // entries with any (or null) workspaceId. When non-null, require exact
            // equality — cross-workspace null-fallback is FORBIDDEN per FR35 / NFR-S4.
            if (workspaceId is not null && entry.WorkspaceId != workspaceId)
            {
                continue;
            }

            survivors.Add((vectorResult, entry));
        }

        if (survivors.Count == 0)
        {
            _logger.LogDebug(
                "SemanticMemoryRetriever — all candidates filtered out for agent {AgentId} (workspace {WorkspaceId}, topK {TopK}). Reason={Reason}, Cause={Cause}.",
                agentId, workspaceId, effectiveTopK, "AllCandidatesFilteredOut", "WorkspaceOrAgeOrOrphan");
            return Array.Empty<MemoryEntry>();
        }

        // Parallel feedback JOIN. Same Task.WhenAll exception pattern.
        var feedbackTasks = survivors
            .Select(s => feedbackService.GetFeedbackForRunAsync(s.Entry.RunId, cancellationToken))
            .ToArray();
        try
        {
            await Task.WhenAll(feedbackTasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            if (AnyTaskObservedCancellation(feedbackTasks))
            {
                throw new OperationCanceledException(cancellationToken);
            }
            var firstFault = FirstFaultException(feedbackTasks);
            _logger.LogWarning(firstFault,
                "SemanticMemoryRetriever — feedback hydration faulted for agent {AgentId}; returning empty (NFR-R3 graceful degradation).",
                agentId);
            return Array.Empty<MemoryEntry>();
        }

        // Project + order + take. Feedback list may be empty (race: row purged
        // between indexing + retrieval); MemoryEntry then carries null Score +
        // null FeedbackComment — the entries-row DigestText is the load-bearing
        // signal, feedback is auxiliary.
        var memories = new List<MemoryEntry>(survivors.Count);
        for (var i = 0; i < survivors.Count; i++)
        {
            var (vectorResult, entry) = survivors[i];
            var feedback = feedbackTasks[i].Result;
            AgentRunFeedback? latest;
            if (feedback.Count == 0)
            {
                _logger.LogDebug(
                    "SemanticMemoryRetriever — feedback empty for surviving candidate RunId {RunId} (agent {AgentId}); MemoryEntry will carry null Score/Comment. Reason={Reason}.",
                    entry.RunId, agentId, "FeedbackEmptyForRunId");
                latest = null;
            }
            else
            {
                latest = feedback[0];
            }

            memories.Add(new MemoryEntry(
                RunId: entry.RunId,
                Summary: entry.DigestText,
                Score: latest?.Score,
                FeedbackComment: latest?.Comment,
                When: entry.CreatedUtc,
                SimilarityScore: vectorResult.Score));
        }

        return memories
            .OrderByDescending(m => m.SimilarityScore)
            .Take(effectiveTopK)
            .ToList();
    }

    private static string ConcatenateMessages(IReadOnlyList<ChatMessage> messages)
    {
        if (messages.Count == 0)
        {
            return string.Empty;
        }

        var segments = new List<string>(messages.Count);
        foreach (var message in messages)
        {
            var text = message.Text;
            if (!string.IsNullOrWhiteSpace(text))
            {
                segments.Add(text);
            }
        }
        return string.Join("\n\n", segments);
    }

    private bool TryExtractMetadata(
        IDictionary<string, object>? metadata,
        string documentId,
        out Guid agentId,
        out string runId)
    {
        agentId = Guid.Empty;
        runId = string.Empty;

        if (metadata is null
            || !metadata.TryGetValue("agentId", out var agentIdRaw)
            || !metadata.TryGetValue("runId", out var runIdRaw))
        {
            _logger.LogDebug(
                "SemanticMemoryRetriever — vector store candidate missing metadata key for DocumentId {DocumentId}. Reason={Reason}.",
                documentId, "MissingMetadataKey");
            return false;
        }

        var agentIdString = agentIdRaw as string ?? agentIdRaw?.ToString();
        if (agentIdString is null || !Guid.TryParse(agentIdString, out agentId))
        {
            _logger.LogWarning(
                "SemanticMemoryRetriever — vector store candidate has unparseable agentId metadata value '{AgentIdRaw}' for DocumentId {DocumentId}. Reason={Reason}.",
                agentIdString, documentId, "UnparseableMetadataAgentId");
            return false;
        }

        var runIdString = runIdRaw as string ?? runIdRaw?.ToString();
        if (string.IsNullOrEmpty(runIdString))
        {
            _logger.LogDebug(
                "SemanticMemoryRetriever — vector store candidate has empty runId metadata for DocumentId {DocumentId}. Reason={Reason}.",
                documentId, "MissingMetadataKey");
            return false;
        }

        runId = runIdString;
        return true;
    }

    private static bool AnyTaskObservedCancellation<T>(Task<T>[] tasks)
    {
        // Real token-driven cancellation lands a task as IsCanceled=true (no
        // exception inside InnerExceptions). Synthetic Task.FromException(new
        // OCE()) lands as IsFaulted=true with the OCE inside Exception.
        // InnerExceptions. Both shapes must propagate cancellation — checking
        // only IsFaulted (the original Story 3.2 implementation) silently
        // swallowed real token cancellation in mixed-fault scenarios where
        // Task.WhenAll rethrew the non-OCE faulted exception first. Code-review
        // 2026-05-14 HIGH finding.
        foreach (var t in tasks)
        {
            if (t.IsCanceled)
            {
                return true;
            }
            if (!t.IsFaulted)
            {
                continue;
            }
            foreach (var inner in t.Exception!.InnerExceptions)
            {
                if (inner is OperationCanceledException)
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static Exception? FirstFaultException<T>(Task<T>[] tasks)
    {
        foreach (var t in tasks)
        {
            if (t.IsFaulted && t.Exception is not null && t.Exception.InnerExceptions.Count > 0)
            {
                return t.Exception.InnerExceptions[0];
            }
        }
        return null;
    }
}
