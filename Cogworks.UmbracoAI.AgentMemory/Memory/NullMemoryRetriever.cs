using Microsoft.Extensions.AI;

namespace Cogworks.UmbracoAI.AgentMemory.Memory;

/// <summary>
/// Placeholder retriever that returns no memories. Retained post Story 3.2 for
/// use in Story 3.3's <c>MemoryInjectionMiddleware</c> per-agent opt-out unit
/// tests (NSubstitute pattern — the test asserts
/// <c>_retriever.DidNotReceiveWithAnyArgs().RetrieveSimilarAsync(...)</c>
/// against this placeholder when an opted-out agent is exercised, per NFR-R4).
/// Production registrations route through <see cref="SemanticMemoryRetriever"/>
/// — see <see cref="Composing.AgentMemoryComposer"/>.
/// </summary>
internal sealed class NullMemoryRetriever : IMemoryRetriever
{
    public Task<IReadOnlyList<MemoryEntry>> RetrieveSimilarAsync(
        Guid agentId,
        Guid? workspaceId,
        IReadOnlyList<ChatMessage> currentMessages,
        int topK,
        CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<MemoryEntry>>(Array.Empty<MemoryEntry>());
}
