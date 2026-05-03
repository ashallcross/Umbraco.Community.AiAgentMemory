namespace Cogworks.UmbracoAI.AgentMemory.Runs;

/// <summary>
/// Captured shape of one tool call performed during an agent run.
/// </summary>
public sealed record ToolCallRecord(
    string ToolId,
    string ArgumentsJson,
    string? ResultJson,
    bool IsError,
    TimeSpan Duration);
