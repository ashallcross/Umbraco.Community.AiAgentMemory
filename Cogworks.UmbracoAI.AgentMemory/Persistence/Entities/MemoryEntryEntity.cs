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
    /// Memory digest for the run. In v0.1, this is the raw joined run text
    /// (<c>PromptSnapshotJoined + ResponseSnapshotJoined + feedback comment</c>)
    /// truncated to <c>AgentMemoryOptions.DigestMaxChars</c> — NOT an LLM
    /// summary. The "Digest" name is retained as the architecture v1 column
    /// name; the contents are raw joined text per PRD scope cut. Story 3.1
    /// owns the background indexer that writes this column.
    /// </summary>
    public string DigestText { get; set; } = string.Empty;

    /// <summary>
    /// Identifier of the corresponding embedding row in <c>IAIVectorStore</c>
    /// (the document key under index alias <c>cogworks-agent-memory</c>).
    /// </summary>
    public string EmbeddingRef { get; set; } = string.Empty;

    /// <summary>
    /// Lifecycle ordinal — maps onto <see cref="Memory.IndexingStatus"/>
    /// (Story 3.1): <c>0 = Pending</c>, <c>1 = Embedded</c>, <c>2 = Failed</c>.
    /// Stored as <see cref="int"/> for EF Core friendliness; the
    /// <see cref="Memory.IndexingStatus"/> enum is the code-side convenience
    /// layer the indexer maps through (mirrors Story 2.1's
    /// <c>FeedbackScore</c> ↔ <c>int Score</c> mapping).
    /// </summary>
    public int IndexingStatus { get; set; }

    public string? IndexingError { get; set; }

    public DateTime? EmbeddedUtc { get; set; }

    public DateTime CreatedUtc { get; set; }
}
