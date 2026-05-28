using Cogworks.UmbracoAI.AgentMemory.Feedback;
using Cogworks.UmbracoAI.AgentMemory.Runs;

namespace Cogworks.UmbracoAI.AgentMemory.Memory;

/// <summary>
/// Generates a compact textual digest of an agent run for storage and retrieval.
/// Production implementation uses an <see cref="Microsoft.Extensions.AI.IChatClient"/>
/// (cheap fast model) to summarise input + output + tool calls + feedback.
/// </summary>
public interface IMemoryDigestService
{
    /// <summary>
    /// Produce a digest summarising the run, optionally incorporating editor feedback.
    /// </summary>
    Task<string> GenerateDigestAsync(
        AgentRunRecord run,
        IReadOnlyList<AgentRunFeedback> feedback,
        CancellationToken cancellationToken);
}
