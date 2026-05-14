using Cogworks.UmbracoAI.AgentMemory.Persistence.Entities;

namespace Cogworks.UmbracoAI.AgentMemory.Persistence.Repositories;

/// <summary>
/// Read/write surface over <see cref="MemoryEntryEntity"/>. Internal-visibility
/// interface introduced at Story 3.1 so the Singleton
/// <see cref="Memory.FeedbackIndexer"/> can mock it in unit tests
/// (<see cref="EFCoreMemoryEntryRepository"/> is <c>sealed</c> — NSubstitute
/// proxies by inheritance and cannot substitute a sealed type, so the interface
/// is the canonical seam for test doubles). Story 3.2's
/// <c>SemanticMemoryRetriever</c> also consumes via this interface for
/// forward-compat.
/// </summary>
internal interface IMemoryEntryRepository
{
    /// <summary>
    /// Returns the entry keyed by <paramref name="runId"/> +
    /// <paramref name="agentId"/>, or <see langword="null"/> if none exists.
    /// The supersede / multi-editor flow uses this lookup to decide
    /// <see cref="AddAsync"/> vs <see cref="UpdateAsync"/>.
    /// </summary>
    Task<MemoryEntryEntity?> FindByRunIdAndAgentIdAsync(
        string runId,
        Guid agentId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Inserts a new entry row.
    /// </summary>
    Task AddAsync(MemoryEntryEntity entity, CancellationToken cancellationToken);

    /// <summary>
    /// Updates an existing entry row in place — preserves
    /// <see cref="MemoryEntryEntity.Id"/>, overwrites mutable fields
    /// (<c>DigestText</c>, <c>EmbeddingRef</c>, <c>IndexingStatus</c>,
    /// <c>IndexingError</c>, <c>EmbeddedUtc</c>).
    /// </summary>
    Task UpdateAsync(MemoryEntryEntity entity, CancellationToken cancellationToken);

    /// <summary>
    /// Returns all entries for the given run, ordered by <c>CreatedUtc</c>
    /// descending. Empty list when no entries exist. Story 3.2's retriever
    /// joins on this surface.
    /// </summary>
    Task<IReadOnlyList<MemoryEntryEntity>> GetByRunIdAsync(
        string runId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns up to <paramref name="take"/> most-recent entries for
    /// <paramref name="agentId"/>, ordered by <c>CreatedUtc</c> descending.
    /// <paramref name="take"/> is clamped to the inclusive range
    /// <c>[0, 100]</c> non-throwingly (mirrors
    /// <c>IAgentRunReader.GetRecentRunsForAgentAsync</c>); <paramref name="take"/>
    /// <c>&lt;= 0</c> returns empty list; <c>&gt; 100</c> clamps to 100. Story
    /// 3.2 consumer.
    /// </summary>
    Task<IReadOnlyList<MemoryEntryEntity>> GetRecentByAgentIdAsync(
        Guid agentId,
        int take,
        CancellationToken cancellationToken);
}
