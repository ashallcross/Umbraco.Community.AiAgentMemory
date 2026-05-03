namespace Cogworks.UmbracoAI.AgentMemory.Runs;

/// <summary>
/// Outcome of an agent run.
/// </summary>
public enum AgentRunStatus
{
    Succeeded = 0,
    Failed = 1,
    Cancelled = 2,
}
