using Umbraco.Community.AiAgentMemory.Feedback;

namespace Umbraco.Community.AiAgentMemory.Memory;

/// <summary>
/// A single past-run memory considered for injection into a future agent run.
/// </summary>
/// <param name="RunId">
/// Run identifier — semantically holds the upstream
/// <c>Metadata["Umbraco.AI.Agent.ThreadId"]</c> per Story 2.3 Path (b)
/// (project-context.md § Schema RunId Column). Same shape as
/// <see cref="Persistence.Entities.MemoryEntryEntity.RunId"/> and
/// <see cref="AgentRunFeedback.RunId"/> — <see cref="string"/>, NOT <see cref="System.Guid"/>.
/// </param>
/// <param name="Summary">
/// The entries-row <c>DigestText</c> — in v0.1, raw joined run text
/// (<c>PromptSnapshotJoined + ResponseSnapshotJoined + feedback comment</c>)
/// truncated to <see cref="Configuration.AgentMemoryOptions.DigestMaxChars"/>,
/// NOT an LLM-summarised digest. Story 3.3's middleware uses this for the
/// "Lessons from past runs" system message.
/// </param>
/// <param name="Score">
/// Most-recent editor feedback score for this run (<c>CreatedUtc DESC</c> per
/// Story 2.1). <see langword="null"/> if no feedback row exists (race —
/// purged between indexing + retrieval; or NFR-R3 swallow returning empty).
/// </param>
/// <param name="FeedbackComment">
/// Most-recent editor comment, or <see langword="null"/> if the feedback row
/// has <c>Comment IS NULL</c> OR no feedback exists.
/// </param>
/// <param name="When">
/// Entries-row <c>CreatedUtc</c> — display-ordering field (consistent with
/// Story 2.1's surface) rather than the indexing-time <c>EmbeddedUtc</c>.
/// </param>
/// <param name="SimilarityScore">
/// Cosine similarity score returned by <c>IAIVectorStore.SearchAsync</c>.
/// Higher = more similar. Range is provider-dependent; OpenAI
/// <c>text-embedding-3-small</c> (cosine over <c>TensorPrimitives</c> in
/// <c>EFCoreAIVectorStore</c>) empirically <c>[0, 1]</c> for non-negative
/// real-valued embeddings — observed <c>[0.37, 0.66]</c> in Spike 0.B's
/// 100-entry test. Providers emitting signed cosine may land in <c>[-1, 1]</c>.
/// See <c>0-b-spike-outcome.md</c> § (d) + § Spec drift note 2.
/// </param>
public sealed record MemoryEntry(
    string RunId,
    string Summary,
    FeedbackScore? Score,
    string? FeedbackComment,
    DateTime When,
    double SimilarityScore);
