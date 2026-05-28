using Umbraco.Community.AiAgentMemory.Persistence.Entities;

namespace Umbraco.Community.AiAgentMemory.Persistence.Repositories;

/// <summary>
/// Read/write surface over <see cref="MemoryEntryEntity"/>. Introduced at
/// Story 3.1 so the Singleton <see cref="Memory.FeedbackIndexer"/> can mock
/// it in unit tests (<see cref="EFCoreMemoryEntryRepository"/> is
/// <c>sealed</c> — NSubstitute proxies by inheritance and cannot substitute
/// a sealed type, so the interface is the canonical seam for test doubles).
/// Story 3.2's <c>SemanticMemoryRetriever</c> also consumes via this
/// interface for forward-compat.
/// </summary>
/// <remarks>
/// <para>
/// <b>Visibility (Story 4.9 DRIFT-4.9-1):</b> originally declared
/// <c>internal interface</c> at Story 3.1 because all consumers were
/// internal (<c>FeedbackIndexer</c>, <c>SemanticMemoryRetriever</c>).
/// Story 4.9 introduces a public Management-API consumer
/// (<c>MemoryEntriesReadController</c>) which forces the interface up to
/// <c>public</c> per ASP.NET MVC's accessibility rule — a public controller
/// cannot inject an internal interface. Matches the visibility of sibling
/// services <c>IAgentFeedbackService</c> + <c>IAgentRunReader</c> (both
/// public for the same reason). Adopters now have type-level visibility of
/// the interface but cannot construct
/// <see cref="EFCoreMemoryEntryRepository"/> (still
/// <c>internal sealed</c>), and registration stays package-private via
/// <c>AgentMemoryComposer</c>.
/// </para>
/// </remarks>
public interface IMemoryEntryRepository
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

    /// <summary>
    /// Returns up to <paramref name="take"/> most-recent entries across ALL
    /// agents, ordered by <c>CreatedUtc</c> descending. <paramref name="take"/>
    /// clamps to the inclusive range <c>[0, 100]</c> non-throwingly:
    /// <c>&lt;= 0</c> returns empty list; <c>&gt; 100</c> clamps to 100.
    /// Story 4.9 Learning Wall consumer — mirrors
    /// <see cref="GetRecentByAgentIdAsync"/>'s shape minus the agent filter.
    /// </summary>
    Task<IReadOnlyList<MemoryEntryEntity>> GetRecentAcrossAgentsAsync(
        int take,
        CancellationToken cancellationToken);
}
