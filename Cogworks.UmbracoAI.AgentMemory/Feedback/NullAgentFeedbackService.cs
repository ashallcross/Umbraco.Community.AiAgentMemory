namespace Cogworks.UmbracoAI.AgentMemory.Feedback;

/// <summary>
/// Placeholder implementation. Replaced in Week 2 by the EF-backed feedback service.
/// </summary>
internal sealed class NullAgentFeedbackService : IAgentFeedbackService
{
    private const string NotImplementedMessage =
        "IAgentFeedbackService implementation pending. Replace NullAgentFeedbackService registration in AgentMemoryComposer in Week 2.";

    public Task RecordFeedbackAsync(
        Guid runId,
        FeedbackScore score,
        string? comment,
        Guid createdBy,
        CancellationToken cancellationToken)
        => throw new NotImplementedException(NotImplementedMessage);

    public Task<IReadOnlyList<AgentRunFeedback>> GetFeedbackForRunAsync(
        Guid runId,
        CancellationToken cancellationToken)
        => throw new NotImplementedException(NotImplementedMessage);

    public Task<IReadOnlyList<AgentRunFeedback>> GetRecentForAgentAsync(
        Guid agentId,
        int take,
        CancellationToken cancellationToken)
        => throw new NotImplementedException(NotImplementedMessage);
}
