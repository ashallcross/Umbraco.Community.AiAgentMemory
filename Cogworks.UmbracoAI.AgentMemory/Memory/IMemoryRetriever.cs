using Microsoft.Extensions.AI;

namespace Cogworks.UmbracoAI.AgentMemory.Memory;

/// <summary>
/// Finds relevant past runs for the current agent invocation.
/// Production implementation uses semantic similarity over digested run content.
/// </summary>
public interface IMemoryRetriever
{
    /// <summary>
    /// Return up to <paramref name="topK"/> memory entries similar to the current
    /// invocation, scoped to <paramref name="agentId"/>.
    /// </summary>
    Task<IReadOnlyList<MemoryEntry>> RetrieveSimilarAsync(
        Guid agentId,
        IReadOnlyList<ChatMessage> currentMessages,
        int topK,
        CancellationToken cancellationToken);
}
