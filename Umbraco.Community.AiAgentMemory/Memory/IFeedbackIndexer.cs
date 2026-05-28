namespace Umbraco.Community.AiAgentMemory.Memory;

/// <summary>
/// Out-of-band background indexer that, on every successful feedback POST,
/// digests the run text + comment, embeds the digest via Umbraco.AI's
/// <c>IAIEmbeddingService</c>, upserts the vector to <c>IAIVectorStore</c>
/// under index alias <c>cogworks-agent-memory</c>, and writes a
/// <c>cogworks_agent_memory_entries</c> row.
/// </summary>
/// <remarks>
/// <para>
/// <b>Fire-and-forget by design.</b> The foreground feedback POST returns
/// <c>200 OK</c> in ≤500ms (NFR-P2) regardless of indexing delays — the
/// controller calls <see cref="EnqueueIndex"/> after a successful
/// <c>RecordFeedbackAsync</c> and immediately returns. Indexing happens
/// out-of-band on Umbraco's framework-owned <c>IBackgroundTaskQueue</c>;
/// permanent failures are logged + persisted as <c>IndexingStatus = Failed</c>
/// rather than rolled back.
/// </para>
/// <para>
/// <b>Best-effort durability.</b> <c>IBackgroundTaskQueue</c> is a non-durable
/// in-process queue; on host shutdown / cancellation mid-retry, the work item
/// is silently abandoned. The feedback row stays persisted (FR12) but no
/// entries row is written for the abandoned attempt. v0.2 candidate: durable
/// queue with boot-time reconcile.
/// </para>
/// <para>
/// <b>Silent no-op per NFR-R1</b> when Umbraco.AI's embedding pipeline isn't
/// configured (no <c>EmbeddingProfileAlias</c> set, or
/// <c>IAIProfileService.GetProfileByAliasAsync</c> returns null). The feedback
/// row IS still persisted; only the memory-pipeline output is skipped. See
/// Story 5.2 README for adopter-facing surface.
/// </para>
/// </remarks>
public interface IFeedbackIndexer
{
    /// <summary>
    /// Enqueues an indexing job for the given run on Umbraco's
    /// <c>IBackgroundTaskQueue</c>. Synchronous; returns immediately. The
    /// controller invokes this AFTER the feedback row has been persisted.
    /// </summary>
    /// <param name="runId">
    /// Run identifier — matches the <c>cogworks_agent_memory_feedback.RunId</c>
    /// column (semantically the upstream
    /// <c>Metadata["Umbraco.AI.Agent.ThreadId"]</c> per Story 2.3 Path (b)).
    /// </param>
    /// <param name="agentId">Server-resolved agent identifier.</param>
    void EnqueueIndex(string runId, Guid agentId);

    /// <summary>
    /// Executes the indexing pipeline directly. Public for unit-test access
    /// without going through the background queue. The controller does NOT
    /// call this — it calls <see cref="EnqueueIndex"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>OperationCanceledException</c> propagates unwrapped (host shutdown
    /// is lifecycle, not failure). Other exceptions are routed to the
    /// retry-or-fail envelope per AC2.
    /// </para>
    /// </remarks>
    Task IndexAsync(string runId, Guid agentId, CancellationToken cancellationToken);
}
