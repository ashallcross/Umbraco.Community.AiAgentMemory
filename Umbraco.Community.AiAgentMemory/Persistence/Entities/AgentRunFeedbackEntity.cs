namespace Umbraco.Community.AiAgentMemory.Persistence.Entities;

/// <summary>
/// One row of editor feedback against an agent run. Persisted to
/// <see cref="Constants.FeedbackTableName"/>. Story 2.1 owns the read/write
/// surface (<c>IAgentFeedbackService</c>); Story 1.1 only defines the row shape.
/// </summary>
public sealed class AgentRunFeedbackEntity
{
    public Guid Id { get; set; }

    /// <summary>
    /// Run identifier — matches the upstream <c>AIAuditLog.Metadata["Umbraco.AI.Agent.RunId"]</c>
    /// value (string, not Guid; Copilot path only in v0.1 per DRIFT-NEW-5).
    /// </summary>
    public string RunId { get; set; } = string.Empty;

    public Guid AgentId { get; set; }

    /// <summary>
    /// 0 = ThumbsUp, 1 = ThumbsDown, 2 = Neutral. Story 2.1 introduces the
    /// <c>FeedbackScore</c> enum that maps these values.
    /// </summary>
    public int Score { get; set; }

    public string? Comment { get; set; }

    /// <summary>
    /// Authenticated host user GUID (NFR-S7 — never a service account).
    /// </summary>
    public Guid CreatedBy { get; set; }

    public DateTime CreatedUtc { get; set; }

    /// <summary>
    /// Workspace identifier (AR7). Nullable from day 1 — populated when host
    /// supplies workspace context, otherwise null (FR36 + AR24).
    /// </summary>
    public Guid? WorkspaceId { get; set; }
}
