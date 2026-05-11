namespace Cogworks.UmbracoAI.AgentMemory.Persistence.Entities;

/// <summary>
/// One memory entry — digest text + reference to the embedding upserted into
/// <c>IAIVectorStore</c> under index alias <c>cogworks-agent-memory</c>.
/// Persisted to <see cref="Constants.MemoryEntriesTableName"/>. Story 3.1 owns
/// the background indexer that writes these rows; Story 1.1 only defines the
/// row shape.
/// </summary>
public sealed class MemoryEntryEntity
{
    public Guid Id { get; set; }

    /// <summary>
    /// Run identifier this entry was distilled from — matches
    /// <see cref="AgentRunFeedbackEntity.RunId"/>.
    /// </summary>
    public string RunId { get; set; } = string.Empty;

    public Guid AgentId { get; set; }

    /// <summary>
    /// Workspace identifier (AR7). Nullable from day 1 (FR36 + AR24).
    /// </summary>
    public Guid? WorkspaceId { get; set; }

    /// <summary>
    /// LLM-generated summary of the run (Story 3.1's
    /// <c>IMemoryDigestService</c>). Capped by
    /// <c>AgentMemoryOptions.DigestMaxChars</c>.
    /// </summary>
    public string DigestText { get; set; } = string.Empty;

    /// <summary>
    /// Identifier of the corresponding embedding row in <c>IAIVectorStore</c>
    /// (the document key under index alias <c>cogworks-agent-memory</c>).
    /// </summary>
    public string EmbeddingRef { get; set; } = string.Empty;

    /// <summary>
    /// 0 = Pending, 1 = Embedded, 2 = Failed. Story 3.1 introduces the
    /// <c>IndexingStatus</c> enum that maps these values.
    /// </summary>
    public int IndexingStatus { get; set; }

    public string? IndexingError { get; set; }

    public DateTime? EmbeddedUtc { get; set; }

    public DateTime CreatedUtc { get; set; }
}
