using System.Diagnostics;
using Cogworks.UmbracoAI.AgentMemory.Configuration;
using Cogworks.UmbracoAI.AgentMemory.Feedback;
using Cogworks.UmbracoAI.AgentMemory.Persistence.Entities;
using Cogworks.UmbracoAI.AgentMemory.Persistence.Repositories;
using Cogworks.UmbracoAI.AgentMemory.Runs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.AI.Core.Embeddings;
using Umbraco.AI.Core.Models;
using Umbraco.AI.Core.Profiles;
using Umbraco.AI.Search.Core.VectorStore;
using Umbraco.Cms.Core.HostedServices;

namespace Cogworks.UmbracoAI.AgentMemory.Memory;

/// <summary>
/// Background indexer that turns one successful feedback POST into one
/// vector-store row + one <c>cogworks_agent_memory_entries</c> row. Composes
/// on Umbraco's framework-owned <see cref="IBackgroundTaskQueue"/>; Singleton
/// lifetime, per-call <see cref="IServiceScope"/>.
/// </summary>
internal sealed class FeedbackIndexer : IFeedbackIndexer
{
    private static readonly TimeSpan[] RetryDelays =
    {
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60),
    };

    private const int MaxAttempts = 3;
    private const int IndexingErrorMaxChars = 1000;

    // Guard against MaxAttempts / RetryDelays divergence. Bumping MaxAttempts
    // without resizing RetryDelays would otherwise produce an
    // IndexOutOfRangeException at runtime (caught by the outer Exception
    // handler and misreported as an "unexpected non-retry failure").
    static FeedbackIndexer()
    {
        Debug.Assert(
            RetryDelays.Length == MaxAttempts - 1,
            $"RetryDelays.Length ({RetryDelays.Length}) must equal MaxAttempts - 1 ({MaxAttempts - 1}).");
    }

    private readonly IBackgroundTaskQueue _taskQueue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<AgentMemoryOptions> _options;
    private readonly IOptions<AIOptions> _aiOptions;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<FeedbackIndexer> _logger;

    public FeedbackIndexer(
        IBackgroundTaskQueue taskQueue,
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<AgentMemoryOptions> options,
        IOptions<AIOptions> aiOptions,
        TimeProvider timeProvider,
        ILogger<FeedbackIndexer> logger)
    {
        ArgumentNullException.ThrowIfNull(taskQueue);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(aiOptions);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _taskQueue = taskQueue;
        _scopeFactory = scopeFactory;
        _options = options;
        _aiOptions = aiOptions;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public void EnqueueIndex(string runId, Guid agentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        if (agentId == Guid.Empty)
        {
            throw new ArgumentException("agentId must not be Guid.Empty.", nameof(agentId));
        }
        _taskQueue.QueueBackgroundWorkItem(ct => IndexAsync(runId, agentId, ct));
    }

    public async Task IndexAsync(string runId, Guid agentId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        if (agentId == Guid.Empty)
        {
            throw new ArgumentException("agentId must not be Guid.Empty.", nameof(agentId));
        }

        var options = _options.CurrentValue;

        using var scope = _scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;

        // Required-resolve all dependencies — both our own and upstream
        // Umbraco.AI surfaces. The package csproj has a direct
        // <PackageReference Include="Umbraco.AI.Search" /> so adopters
        // automatically receive the upstream DI registrations. If for any
        // reason an adopter excludes one of these, GetRequiredService throws
        // on first invocation — caught by the outer catch, logged Error.
        IAIVectorStore vectorStore;
        IAIEmbeddingService embeddingService;
        IAIProfileService profileService;
        IAgentRunReader runReader;
        IAgentFeedbackService feedbackService;
        IMemoryEntryRepository repository;
        try
        {
            vectorStore = sp.GetRequiredService<IAIVectorStore>();
            embeddingService = sp.GetRequiredService<IAIEmbeddingService>();
            profileService = sp.GetRequiredService<IAIProfileService>();
            runReader = sp.GetRequiredService<IAgentRunReader>();
            feedbackService = sp.GetRequiredService<IAgentFeedbackService>();
            repository = sp.GetRequiredService<IMemoryEntryRepository>();
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex,
                "FeedbackIndexer — upstream service not registered: {Service}. "
                + "Check the Umbraco.AI / Umbraco.AI.Search package graph. "
                + "Run {RunId}, agent {AgentId} will not be indexed.",
                ex.Message, runId, agentId);
            return;
        }

        // Resolve embedding profile alias (alias fallback chain → null ⇒ NFR-R1 silent no-op).
        var alias = options.EmbeddingProfileAlias ?? _aiOptions.Value.DefaultEmbeddingProfileAlias;
        if (string.IsNullOrEmpty(alias))
        {
            _logger.LogDebug(
                "FeedbackIndexer — silent no-op for run {RunId} (agent {AgentId}); Reason={Reason}.",
                runId, agentId, "NoEmbeddingProfileAliasConfigured");
            return;
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
                "FeedbackIndexer — profile lookup failed for alias {Alias} (run {RunId}, agent {AgentId}); skipping.",
                alias, runId, agentId);
            return;
        }
        if (profile is null)
        {
            _logger.LogDebug(
                "FeedbackIndexer — silent no-op for run {RunId} (agent {AgentId}); Reason={Reason}.",
                runId, agentId, "EmbeddingProfileAliasLookupReturnedNull");
            return;
        }

        // Read run records for the ThreadId. v0.1 picks runs[0] (most-recent
        // per StartedUtc DESC); multi-record join is a v0.2 candidate.
        IReadOnlyList<AgentRunRecord> runs;
        try
        {
            runs = await runReader.GetRunsForThreadAsync(runId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "FeedbackIndexer — run-reader threw for run {RunId} (agent {AgentId}); skipping.",
                runId, agentId);
            return;
        }
        if (runs.Count == 0)
        {
            _logger.LogDebug(
                "FeedbackIndexer — no audit-log records for RunId {RunId} (agent {AgentId}); skipping.",
                runId, agentId);
            return;
        }
        var run = runs[0];

        // Read feedback; if empty → Debug + return (defensive guard against NFR-R3 swallow).
        IReadOnlyList<AgentRunFeedback> feedback;
        try
        {
            feedback = await feedbackService.GetFeedbackForRunAsync(runId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "FeedbackIndexer — feedback-service threw for run {RunId} (agent {AgentId}); skipping.",
                runId, agentId);
            return;
        }
        if (feedback.Count == 0)
        {
            _logger.LogDebug(
                "FeedbackIndexer — no feedback rows for RunId {RunId} (agent {AgentId}); skipping.",
                runId, agentId);
            return;
        }
        var latestFeedback = feedback[0];

        // Build digest + deterministic documentId.
        var digest = BuildDigest(run, latestFeedback.Comment, options.DigestMaxChars);
        if (string.IsNullOrWhiteSpace(digest))
        {
            _logger.LogDebug(
                "FeedbackIndexer — empty digest for run {RunId} (agent {AgentId}); skipping. Reason={Reason}.",
                runId, agentId, "EmptyDigestSkipping");
            return;
        }
        var documentId = BuildDocumentId(runId, agentId);
        var metadata = new Dictionary<string, object>
        {
            ["agentId"] = agentId.ToString("D"),
            ["runId"] = runId,
        };

        try
        {
            await EmbedAndUpsertWithRetryAsync(
                embeddingService,
                vectorStore,
                profile.Id,
                digest,
                documentId,
                metadata,
                options.VectorIndex.Alias,
                runId,
                agentId,
                cancellationToken).ConfigureAwait(false);

            // Success path — persist Embedded row.
            await UpsertEntriesRowAsync(
                repository,
                runId,
                agentId,
                digest,
                embeddingRef: documentId,
                status: IndexingStatus.Embedded,
                indexingError: null,
                embeddedUtc: _timeProvider.GetUtcNow().UtcDateTime,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (IndexingPermanentFailureException ex)
        {
            _logger.LogError(ex.InnerException,
                "FeedbackIndexer — embedding/upsert failed permanently after 3 attempts "
                + "for run {RunId} (agent {AgentId}); last error: {LastError}",
                runId, agentId, ex.InnerException?.Message);

            try
            {
                await UpsertEntriesRowAsync(
                    repository,
                    runId,
                    agentId,
                    digest,
                    embeddingRef: string.Empty,
                    status: IndexingStatus.Failed,
                    indexingError: TruncateError(ex.InnerException?.Message),
                    embeddedUtc: null,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception writeEx)
            {
                _logger.LogWarning(writeEx,
                    "FeedbackIndexer — failed to persist Failed row for run {RunId} (agent {AgentId}); storage unavailable.",
                    runId, agentId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "FeedbackIndexer — unexpected non-retry failure for run {RunId} (agent {AgentId}); skipping.",
                runId, agentId);
        }
    }

    /// <summary>
    /// Joins the editor's <paramref name="comment"/>, the agent's
    /// <see cref="AgentRunRecord.ResponseSnapshotJoined"/>, and the original
    /// <see cref="AgentRunRecord.PromptSnapshotJoined"/> into one digest blob
    /// for embedding + storage on the memory entry row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Segment order (AR35, ratified 2026-05-19; Story 4.2):</b>
    /// <c>Comment → ResponseSnapshotJoined → PromptSnapshotJoined</c> joined
    /// with <c>"\n\n"</c>, then truncated to <paramref name="maxChars"/>. Null
    /// or whitespace segments are skipped before the join.
    /// </para>
    /// <para>
    /// The order is information-density-descending so the editor's teaching
    /// signal survives truncation. The comment carries the explicit editor
    /// disagreement; the response carries the agent's reasoning artefact; the
    /// prompt is the LEAST important to preserve under truncation because the
    /// agent already saw it natively in the original run and re-reading the
    /// truncated prefix doesn't help the agent in Run 2.
    /// </para>
    /// <para>
    /// Empirical evidence: Story 3.1's original Prompt → Response → Comment
    /// order under the default 500-char <c>DigestMaxChars</c> budget chopped
    /// the comment off entirely for realistic editorial content (~3 KB prompt
    /// + ~2.6 KB response + ~400 char comment), breaking FR44 semantically.
    /// See <c>4-2-fr44-demo-critical-fixes-digest-widget-render.md</c> § AC1
    /// + AR35 in <c>epics.md</c> § Additional Requirements for the rationale.
    /// </para>
    /// <para>
    /// <b>Edge case:</b> when <paramref name="comment"/> itself exceeds
    /// <paramref name="maxChars"/>, the comment truncates at the cap; the
    /// response and prompt drop out entirely. Acceptable for v0.1 — editorial
    /// brand-voice comments in the demo corpus are typically &lt; 400 chars.
    /// v0.2 LLM-based digest (post-Codegarden) addresses the scale ceiling.
    /// </para>
    /// </remarks>
    private static string BuildDigest(AgentRunRecord run, string? comment, int maxChars)
    {
        var segments = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(comment)) segments.Add(comment!);
        if (!string.IsNullOrWhiteSpace(run.ResponseSnapshotJoined)) segments.Add(run.ResponseSnapshotJoined);
        if (!string.IsNullOrWhiteSpace(run.PromptSnapshotJoined)) segments.Add(run.PromptSnapshotJoined);
        var joined = string.Join("\n\n", segments);
        return joined.Length > maxChars && maxChars > 0
            ? joined.Substring(0, maxChars)
            : joined;
    }

    private static string BuildDocumentId(string runId, Guid agentId)
        => $"{runId}:{agentId:N}";

    private async Task EmbedAndUpsertWithRetryAsync(
        IAIEmbeddingService embeddingService,
        IAIVectorStore vectorStore,
        Guid profileId,
        string digest,
        string documentId,
        IDictionary<string, object> metadata,
        string indexName,
        string runId,
        Guid agentId,
        CancellationToken cancellationToken)
    {
        Exception? lastException = null;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                var embedding = await embeddingService.GenerateEmbeddingAsync(
                    configure => configure
                        // Required by AIEmbeddingBuilder.Validate() in Umbraco.AI.Core
                        // 1.10.x+ — a URL-safe observability identifier for this
                        // inline-embedding call-site (not the profile alias). Pinned
                        // to the package's brand-anchor so adopter audit-log surfaces
                        // attribute the row back to our memory pipeline.
                        .WithAlias("cogworks-agent-memory-feedback-indexer")
                        .WithProfile(profileId)
                        .WithDescription("Cogworks.UmbracoAI.AgentMemory FeedbackIndexer digest"),
                    digest,
                    cancellationToken).ConfigureAwait(false);

                await vectorStore.UpsertAsync(
                    indexName: indexName,
                    documentId: documentId,
                    culture: null,
                    chunkIndex: 0,
                    vector: embedding.Vector,
                    metadata: metadata,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastException = ex;
                _logger.LogWarning(ex,
                    "FeedbackIndexer — attempt {Attempt}/{MaxAttempts} failed for run {RunId} (agent {AgentId}): {Error}",
                    attempt, MaxAttempts, runId, agentId, ex.Message);

                if (attempt < MaxAttempts)
                {
                    await Task.Delay(RetryDelays[attempt - 1], _timeProvider, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }

        throw new IndexingPermanentFailureException(lastException!);
    }

    private async Task UpsertEntriesRowAsync(
        IMemoryEntryRepository repository,
        string runId,
        Guid agentId,
        string digest,
        string embeddingRef,
        IndexingStatus status,
        string? indexingError,
        DateTime? embeddedUtc,
        CancellationToken cancellationToken)
    {
        var existing = await repository.FindByRunIdAndAgentIdAsync(runId, agentId, cancellationToken)
            .ConfigureAwait(false);
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;

        if (existing is null)
        {
            var entry = new MemoryEntryEntity
            {
                Id = Guid.NewGuid(),
                RunId = runId,
                AgentId = agentId,
                WorkspaceId = null,
                DigestText = digest,
                EmbeddingRef = embeddingRef,
                IndexingStatus = (int)status,
                IndexingError = indexingError,
                EmbeddedUtc = embeddedUtc,
                CreatedUtc = nowUtc,
            };
            await repository.AddAsync(entry, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            existing.DigestText = digest;
            existing.EmbeddingRef = embeddingRef;
            existing.IndexingStatus = (int)status;
            existing.IndexingError = indexingError;
            existing.EmbeddedUtc = embeddedUtc;
            await repository.UpdateAsync(existing, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string? TruncateError(string? message)
    {
        if (message is null)
        {
            return null;
        }
        return message.Length <= IndexingErrorMaxChars
            ? message
            : message.Substring(0, IndexingErrorMaxChars);
    }

    private sealed class IndexingPermanentFailureException : Exception
    {
        public IndexingPermanentFailureException(Exception inner)
            : base("FeedbackIndexer exhausted retry budget", inner)
        {
        }
    }
}
