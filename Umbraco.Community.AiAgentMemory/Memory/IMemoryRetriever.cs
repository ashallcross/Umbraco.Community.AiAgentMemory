using Microsoft.Extensions.AI;

namespace Umbraco.Community.AiAgentMemory.Memory;

/// <summary>
/// Finds relevant past runs for the current agent invocation. Production
/// implementation (<see cref="SemanticMemoryRetriever"/>, Story 3.2) uses
/// semantic similarity over <see cref="Persistence.Entities.MemoryEntryEntity.DigestText"/>
/// digests embedded into <c>IAIVectorStore</c> under alias
/// <c>cogworks-agent-memory</c>.
/// </summary>
public interface IMemoryRetriever
{
    /// <summary>
    /// Returns up to <paramref name="topK"/> memory entries semantically similar
    /// to the current invocation, scoped to <paramref name="agentId"/> + (optionally)
    /// <paramref name="workspaceId"/>, recent enough (<see cref="Configuration.AgentMemoryOptions.MaxMemoryAgeDays"/>),
    /// and at or above <see cref="Configuration.AgentMemoryOptions.EligibilityThreshold"/>
    /// cosine similarity.
    /// </summary>
    /// <param name="agentId">FR21 — only memories produced for this agent are returned.</param>
    /// <param name="workspaceId">
    /// FR22 + FR36 — when non-<see langword="null"/>, only memories whose entries-row
    /// <see cref="Persistence.Entities.MemoryEntryEntity.WorkspaceId"/> matches exactly
    /// are returned (cross-workspace null-fallback is forbidden per FR35 / NFR-S4
    /// — the load-bearing multi-tenant isolation guarantee). When <see langword="null"/>,
    /// entries with <see cref="Persistence.Entities.MemoryEntryEntity.WorkspaceId"/>
    /// = <see langword="null"/> are tolerated (FR36 null-tolerance verified empirically
    /// by Spike 0.B).
    /// </param>
    /// <param name="currentMessages">
    /// The pending chat invocation's prior messages — the retriever concatenates
    /// their text content into a single embedding input. Must NOT be <see langword="null"/>
    /// (caller-must-supply discipline matches Story 1.2 <c>IAgentRunReader</c>).
    /// Empty or all-whitespace lists return an empty result defensively (zero-vector
    /// embeddings are meaningless).
    /// </param>
    /// <param name="topK">
    /// FR20 — clamped to <c>[1, 10]</c>; values &gt; 10 silently clamp to 10;
    /// values &lt;= 0 short-circuit to empty list without invoking the embedding /
    /// vector-store services.
    /// </param>
    /// <param name="cancellationToken">
    /// Propagates unwrapped per Stories 1.2 / 2.1 / 3.1 cancellation discipline;
    /// never swallowed into the graceful-degradation path.
    /// </param>
    /// <returns>
    /// Up to <paramref name="topK"/> entries ordered by similarity descending.
    /// **Never <see langword="null"/>; never throws** out of the public method
    /// except <see cref="OperationCanceledException"/> (NFR-R3 graceful-degradation
    /// discipline — embed / search / repository / feedback-service transient failures
    /// log <c>Warning</c> + return empty list; no retry on the retrieval hot path).
    /// Story 3.3's <c>MemoryInjectionMiddleware</c> passes the chat call through
    /// unchanged on empty result per FR26.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>Silent no-op when the AI section UI is not configured (NFR-R1):</b>
    /// if no embedding profile alias resolves (neither
    /// <see cref="Configuration.AgentMemoryOptions.EmbeddingProfileAlias"/> nor
    /// the host's <c>AIOptions.DefaultEmbeddingProfileAlias</c>), OR if
    /// <c>IAIProfileService.GetProfileByAliasAsync</c> returns <see langword="null"/>,
    /// the retriever returns an empty list and logs <c>Debug</c>. Documented in
    /// the Story 5.2 README as the adopter-onboarding prerequisite.
    /// </para>
    /// <para>
    /// <b>Similarity score range:</b> <see cref="MemoryEntry.SimilarityScore"/> is
    /// provider-dependent; OpenAI <c>text-embedding-3-small</c> (cosine over
    /// <c>TensorPrimitives</c> in <c>EFCoreAIVectorStore</c>) empirically lands in
    /// <c>[0, 1]</c> for non-negative real-valued embeddings — observed
    /// <c>[0.37, 0.66]</c> in Spike 0.B's 100-entry test. Adopters using providers
    /// that emit signed cosine see <c>[-1, 1]</c>; tune
    /// <see cref="Configuration.AgentMemoryOptions.EligibilityThreshold"/> per provider.
    /// See <c>0-b-spike-outcome.md</c> § (d) + § Spec drift note 2.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<MemoryEntry>> RetrieveSimilarAsync(
        Guid agentId,
        Guid? workspaceId,
        IReadOnlyList<ChatMessage> currentMessages,
        int topK,
        CancellationToken cancellationToken);
}
