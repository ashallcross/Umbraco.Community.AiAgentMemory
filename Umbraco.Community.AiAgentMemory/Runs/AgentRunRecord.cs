namespace Cogworks.UmbracoAI.AgentMemory.Runs;

/// <summary>
/// Projection of one agent chat call. In <c>Umbraco.AI.Agent.Core 1.9.0</c> each
/// <c>IChatClient.GetResponseAsync</c> invocation produces one <c>AIAuditLog</c> row carrying its
/// own <c>Metadata["Umbraco.AI.Agent.RunId"]</c> — including tool-call follow-up rounds within a
/// single user message. So an <c>AgentRunRecord</c> is <b>1:1 with one chat call</b>, not with the
/// user's full question-to-answer cycle (DRIFT-NEW-3, ratified at AR28).
///
/// <para>
/// <b>Conversation-level grouping key:</b> <see cref="ThreadId"/>. Adopters wanting "all the
/// agent's calls during this user's conversation" should group by <c>ThreadId</c> across recent
/// records. Story 3.x's memory injection middleware queries by <c>ThreadId</c> for cross-call
/// context; <c>RunId</c> is the per-chat-call identity.
/// </para>
///
/// <para>
/// The <c>MIN/MAX/SUM/Joined</c> aggregation rules below remain in code as a future-proof seam:
/// they degenerate cleanly to single-row groups today, and continue to work if upstream later
/// emits multi-row-per-RunId groups (e.g., a future "agent run rollup" row — PR-Upstream-3
/// candidate).
/// </para>
/// </summary>
public sealed record AgentRunRecord(
    string RunId,                           // Metadata["Umbraco.AI.Agent.RunId"] — per-chat-call upstream identifier (Copilot path; null otherwise pre-Fork-(i))
    Guid AgentId,                           // FeatureId
    int? AgentVersion,                      // FeatureVersion
    DateTime StartedUtc,                    // MIN(StartTime) — degenerate to row's StartTime in v0.1
    DateTime? CompletedUtc,                 // MAX(EndTime)   — degenerate to row's EndTime in v0.1
    AgentRunStatus AggregateStatus,         // worst-status across rows per precedence rule below
    string? Error,                          // first non-null ErrorMessage
    string? PromptSnapshotJoined,           // ordered concatenation of PromptSnapshot
    string? ResponseSnapshotJoined,         // ordered concatenation of ResponseSnapshot
    int? TokenCountInput,                   // SUM(InputTokens)
    int? TokenCountOutput,                  // SUM(OutputTokens)
    // Conversation-level / Automate-workflow-run-level grouping key. Also
    // semantically the value persisted as the package's
    // cogworks_agent_memory_feedback.RunId column per Story 2.3 Path (b)
    // decision (the editor's modal hands us a ThreadId; we record it under
    // the RunId column to keep the existing schema name).
    string? ThreadId,                       // Metadata["Umbraco.AI.Agent.ThreadId"]
    string? UserId,
    string? TraceId);
