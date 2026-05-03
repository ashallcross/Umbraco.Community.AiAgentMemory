using Cogworks.UmbracoAI.AgentMemory.Feedback;
using Cogworks.UmbracoAI.AgentMemory.Runs;

namespace Cogworks.UmbracoAI.AgentMemory.Memory;

/// <summary>
/// Placeholder digest service. Replaced in Week 2 by <c>ChatClientMemoryDigestService</c>
/// that uses an <see cref="Microsoft.Extensions.AI.IChatClient"/> to summarise runs.
/// </summary>
internal sealed class NullMemoryDigestService : IMemoryDigestService
{
    public Task<string> GenerateDigestAsync(
        AgentRunRecord run,
        IReadOnlyList<AgentRunFeedback> feedback,
        CancellationToken cancellationToken)
        => Task.FromResult(string.Empty);
}
