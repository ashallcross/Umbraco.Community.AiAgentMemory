namespace Cogworks.UmbracoAI.AgentMemory.Web.Api.Models;

/// <summary>
/// One per-iteration entry returned by
/// <c>GET /umbraco/management/api/v1/cogworks-agent-memory/runs/{threadId}/siblings</c>.
/// Story 4.12 — per-iteration picker inside the Run Detail modal so editors can
/// flip between all N sibling agent runs from the same Automate workflow (For
/// Each step) without leaving the modal.
/// </summary>
/// <remarks>
/// <para>
/// <b>Identity model (Story 4.12 LD#8 ratified 2026-05-21 by Winston):</b>
/// <see cref="ThreadId"/> is the workflow-run-level grouping key
/// (<c>Metadata.Umbraco.AI.Agent.ThreadId</c>) — i.e. the value the
/// Bellissima <c>Ua.Modal.RunDetail</c> modal hands the widget in
/// <c>modalContext.data.runId</c>. <see cref="RunId"/> is the per-iteration
/// agent-invocation key (<c>Metadata.Umbraco.AI.Agent.RunId</c>) — distinct
/// per For Each iteration. The widget keeps both explicit so feedback POSTs
/// can target a specific iteration (selectedRunId) while preserving the
/// workflow ThreadId context.
/// </para>
/// <para>
/// <b>Label shape locked at architect ratification (agent-agnostic):</b> the
/// picker counter renders <c>Iteration N of M · {hh:mm:ss}</c> where N is the
/// 1-indexed position in the ASC-sorted siblings list (oldest first) and
/// <see cref="StartedUtc"/> is rendered in the user's local timezone. No
/// content-type-specific vocabulary in v0.1 — those would require per-agent
/// extraction logic and don't generalise across the package's target agent
/// types (brand-voice audit, FAQ generation, customer-service drafting, code
/// review, translation review, classification, etc.).
/// </para>
/// </remarks>
public sealed record AgentRunSiblingResponse(
    string ThreadId,
    string RunId,
    DateTime StartedUtc,
    bool IsCurrent);
