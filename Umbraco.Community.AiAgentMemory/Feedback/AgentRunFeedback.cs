namespace Umbraco.Community.AiAgentMemory.Feedback;

/// <summary>
/// Editor feedback recorded against one agent run.
/// </summary>
public sealed record AgentRunFeedback(
    Guid Id,
    string RunId,
    Guid AgentId,
    FeedbackScore Score,
    string? Comment,
    Guid CreatedBy,
    DateTime CreatedUtc);
