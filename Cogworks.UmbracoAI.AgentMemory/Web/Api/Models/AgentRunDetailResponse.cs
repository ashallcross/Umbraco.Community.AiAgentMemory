namespace Cogworks.UmbracoAI.AgentMemory.Web.Api.Models;

/// <summary>
/// Read-only projection of an agent run's identity + parsed structured output.
/// Returned by <c>GET /umbraco/management/api/v1/cogworks-agent-memory/runs/{runId}</c>
/// to the editor feedback widget so the editor sees WHAT the agent flagged
/// before submitting a thumbs-up/down (Story 4.2, closes DRIFT-4.1-12).
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
/// <b>Structured output fields:</b> <see cref="Score"/>, <see cref="Issues"/>,
/// <see cref="Suggestions"/> are parsed defensively from
/// <see cref="Runs.AgentRunRecord.ResponseSnapshotJoined"/>. When the response
/// snapshot is null/empty or fails JSON-parse, the endpoint returns 200 OK with
/// <c>Score = null</c> + empty issues/suggestions arrays (NFR-R1 graceful
/// degradation; the widget renders an "Agent output unavailable" message but
/// still shows the feedback form).
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
    IReadOnlyList<string> Suggestions);

/// <summary>
/// A single flagged item from the agent's structured output, surfaced to the
/// editor in the feedback widget's "Agent output" panel.
/// </summary>
public sealed record AgentRunDetailIssue(string Text, string? Reason);
