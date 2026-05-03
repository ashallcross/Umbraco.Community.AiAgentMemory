using Microsoft.Extensions.AI;

namespace Cogworks.UmbracoAI.AgentMemory.Runs;

/// <summary>
/// Persistent record of one agent run.
/// </summary>
public sealed record AgentRunRecord(
    Guid Id,
    Guid AgentId,
    int AgentVersion,
    DateTime StartedUtc,
    DateTime CompletedUtc,
    AgentRunStatus Status,
    string? Error,
    IReadOnlyList<ChatMessage> Input,
    IReadOnlyList<ChatMessage> Output,
    IReadOnlyList<ToolCallRecord> ToolCalls,
    int? TokenCountInput,
    int? TokenCountOutput,
    decimal? Cost,
    string InitiatedBy,
    Guid? InitiatorId,
    Guid? CorrelationId);
