using Microsoft.Extensions.AI;

namespace Cogworks.UmbracoAI.AgentMemory.Memory;

/// <summary>
/// Placeholder retriever that returns no memories. Replaced in Week 2 by
/// <c>SemanticMemoryRetriever</c> built on <c>Umbraco.AI.Search</c>'s vector store.
/// Returning empty (rather than throwing) keeps middleware composition safe
/// during incremental development.
/// </summary>
internal sealed class NullMemoryRetriever : IMemoryRetriever
{
    public Task<IReadOnlyList<MemoryEntry>> RetrieveSimilarAsync(
        Guid agentId,
        IReadOnlyList<ChatMessage> currentMessages,
        int topK,
        CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<MemoryEntry>>(Array.Empty<MemoryEntry>());
}
