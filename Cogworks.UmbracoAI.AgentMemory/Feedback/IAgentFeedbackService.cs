namespace Cogworks.UmbracoAI.AgentMemory.Feedback;

/// <summary>
/// Records and queries editor feedback against agent runs.
/// </summary>
public interface IAgentFeedbackService
{
    /// <summary>
    /// Record a single feedback signal against a run.
    /// </summary>
    Task RecordFeedbackAsync(
        Guid runId,
        FeedbackScore score,
        string? comment,
        Guid createdBy,
        CancellationToken cancellationToken);

    /// <summary>
    /// Get all feedback recorded against a single run.
    /// </summary>
    Task<IReadOnlyList<AgentRunFeedback>> GetFeedbackForRunAsync(
        Guid runId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Get the most recent feedback entries for a given agent.
    /// </summary>
    Task<IReadOnlyList<AgentRunFeedback>> GetRecentForAgentAsync(
        Guid agentId,
        int take,
        CancellationToken cancellationToken);
}
