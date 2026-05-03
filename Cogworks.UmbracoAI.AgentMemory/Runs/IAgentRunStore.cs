namespace Cogworks.UmbracoAI.AgentMemory.Runs;

/// <summary>
/// Persistence boundary for agent runs.
/// Implemented in Week 1 by an EF-backed store; replaced by the upstream
/// <c>Umbraco.AI</c> primitive once that PR merges (see planning doc 09).
/// </summary>
public interface IAgentRunStore
{
    /// <summary>
    /// Persist a completed agent run. Returns the new run id.
    /// </summary>
    Task<Guid> RecordRunAsync(AgentRunRecord run, CancellationToken cancellationToken);

    /// <summary>
    /// Look up a single run by id.
    /// </summary>
    Task<AgentRunRecord?> GetRunAsync(Guid runId, CancellationToken cancellationToken);

    /// <summary>
    /// Get the most recent runs for a given agent, newest first.
    /// </summary>
    Task<IReadOnlyList<AgentRunRecord>> GetRecentRunsForAgentAsync(
        Guid agentId,
        int take,
        CancellationToken cancellationToken);
}
