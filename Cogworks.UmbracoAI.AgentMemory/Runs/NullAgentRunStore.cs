namespace Cogworks.UmbracoAI.AgentMemory.Runs;

/// <summary>
/// Placeholder implementation that throws on any call.
/// Replaced in Week 1 by the EF-backed <c>AgentRunStore</c>.
/// Registered by <see cref="Composing.AgentMemoryComposer"/> so the package
/// composes cleanly even before the real implementation lands.
/// </summary>
internal sealed class NullAgentRunStore : IAgentRunStore
{
    private const string NotImplementedMessage =
        "IAgentRunStore implementation pending. Replace NullAgentRunStore registration in AgentMemoryComposer with the EF-backed AgentRunStore (Week 1 of the sprint plan).";

    public Task<Guid> RecordRunAsync(AgentRunRecord run, CancellationToken cancellationToken)
        => throw new NotImplementedException(NotImplementedMessage);

    public Task<AgentRunRecord?> GetRunAsync(Guid runId, CancellationToken cancellationToken)
        => throw new NotImplementedException(NotImplementedMessage);

    public Task<IReadOnlyList<AgentRunRecord>> GetRecentRunsForAgentAsync(
        Guid agentId,
        int take,
        CancellationToken cancellationToken)
        => throw new NotImplementedException(NotImplementedMessage);
}
