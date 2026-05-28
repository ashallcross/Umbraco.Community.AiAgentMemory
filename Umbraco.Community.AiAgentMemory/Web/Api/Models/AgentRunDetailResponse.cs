namespace Umbraco.Community.AiAgentMemory.Web.Api.Models;

/// <summary>
/// Read-only projection of an agent run's identity + parsed structured output
/// + memory-injection citation surface. Returned by
/// <c>GET /umbraco/management/api/v1/cogworks-agent-memory/runs/{runId}</c> to
/// the editor feedback widget.
/// </summary>
/// <remarks>
/// <para>
/// <b>Run identity fields:</b> <see cref="RunId"/> is the supplied parameter
/// (semantically the upstream <c>Metadata.Umbraco.AI.Agent.ThreadId</c> per
/// the Story 2.3 schema amendment 2026-05-13); <see cref="AgentId"/> + <see cref="RanAtUtc"/>
/// are projected from <see cref="Runs.AgentRunRecord"/>.
/// </para>
/// <para>
/// <b>Optional display fields:</b> <see cref="AgentDisplayName"/> +
/// <see cref="ContentNodeName"/> are emitted as <c>null</c> in v0.1 — the
/// reader doesn't currently surface them cheaply and the widget falls back to
/// "Agent {agentId}" rendering. Lookups via additional services are a v0.2
/// candidate.
/// </para>
/// <para>
/// <b>Structured output fields (Story 4.2, closes DRIFT-4.1-12):</b>
/// <see cref="Score"/>, <see cref="Issues"/>, <see cref="Suggestions"/> are
/// parsed defensively from
/// <see cref="Runs.AgentRunRecord.ResponseSnapshotJoined"/>. When the response
/// snapshot is null/empty or fails JSON-parse, the endpoint returns 200 OK with
/// <c>Score = null</c> + empty issues/suggestions arrays (NFR-R1 graceful
/// degradation; the widget renders an "Agent output unavailable" message but
/// still shows the feedback form).
/// </para>
/// <para>
/// <b>Memory-citation fields (Story 4.5):</b> <see cref="MemoryUsed"/> is
/// <see langword="true"/> when the upstream
/// <see cref="Runs.AgentRunRecord.PromptSnapshotJoined"/> starts with the
/// Story 3.3 memory-injection anchor (<c>[system] Lessons from past runs:</c>);
/// <see cref="CitedMemories"/> carries the parsed bullet entries (one per
/// injected memory, capped at <c>AgentRunReadController.MaxCitedMemories = 10</c>;
/// each entry's <see cref="AgentRunCitedMemory.CommentSnippet"/> truncated at
/// the controller layer to 300 chars with <c>"…"</c> ellipsis). Empty array
/// when no anchor matches OR when the anchor matches but no bullets parse
/// (graceful degradation per NFR-R1; Warning log emitted on malformed-bullet
/// case so Story 3.3 contract drift is surfaced in ops dashboards).
/// </para>
/// <para>
/// <b>Per-iteration selection field (Story 4.12 — picker selectedRunId):</b>
/// <see cref="SelectedRunId"/> carries the per-iteration agent-invocation key
/// (<c>Metadata.Umbraco.AI.Agent.RunId</c>) for the iteration whose detail is
/// being projected. <see langword="null"/> for legacy/non-picker requests
/// (when the controller falls through to <c>runs[0]</c> from the ThreadId
/// group, preserving Story 4.5 byte-compatible behaviour). The
/// <see cref="RunId"/> field continues to carry the supplied ThreadId for
/// backwards compatibility — see Story 4.12 LD#8 + the
/// <c>cogworks_agent_memory_feedback.RunId</c> column dual-semantic note in
/// <c>project-context.md</c>.
/// </para>
/// </remarks>
public sealed record AgentRunDetailResponse(
    string RunId,
    Guid AgentId,
    string? AgentDisplayName,
    string? ContentNodeName,
    DateTime RanAtUtc,
    int? Score,
    IReadOnlyList<AgentRunDetailIssue> Issues,
    IReadOnlyList<string> Suggestions,
    bool MemoryUsed,
    IReadOnlyList<AgentRunCitedMemory> CitedMemories,
    string? SelectedRunId = null);

/// <summary>
/// A single flagged item from the agent's structured output, surfaced to the
/// editor in the feedback widget's "Agent output" panel.
/// </summary>
public sealed record AgentRunDetailIssue(string Text, string? Reason);

/// <summary>
/// One bullet from Story 3.3's "Lessons from past runs" memory-injection block
/// — parsed out of <see cref="Runs.AgentRunRecord.PromptSnapshotJoined"/> by
/// <c>AgentRunReadController.ParseMemoryInjection</c> and surfaced to the
/// widget so the editor sees which past feedback influenced the current run
/// (Story 4.5 Q2a — Memory-used indicator + cited memories).
/// </summary>
/// <remarks>
/// Parser contract per <c>MemoryInjectionMiddleware.BuildMemorySystemMessage</c>
/// at <c>Umbraco.Community.AiAgentMemory/Middleware/MemoryInjectionMiddleware.cs:215-250</c>.
/// Bullet shape: <c>"• Run {first8} {emoji}: {summary} — \"{comment}\""</c>
/// where <c>{first8} = memory.RunId.AsSpan(0, Math.Min(8, RunId.Length))</c>,
/// <c>{emoji} ∈ { "👍", "👎", "•" }</c>, and the
/// <c> — "{comment}"</c> suffix is omitted when the memory's
/// <c>FeedbackComment</c> is null/whitespace.
/// </remarks>
public sealed record AgentRunCitedMemory(
    string RunIdPrefix,
    string Emoji,
    string? CommentSnippet);
