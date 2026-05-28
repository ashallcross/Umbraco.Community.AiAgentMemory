using Umbraco.Community.AiAgentMemory.Feedback;

namespace Umbraco.Community.AiAgentMemory.Web.Api.Models;

/// <summary>
/// POST body for
/// <c>/umbraco/management/api/v1/cogworks-agent-memory/feedback</c>.
/// </summary>
/// <remarks>
/// <para>
/// All fields are required except <see cref="Comment"/> and
/// <see cref="SelectedRunId"/>. The host user identity (<c>CreatedBy</c>) is
/// resolved server-side via <c>IBackOfficeSecurityAccessor</c> and is NOT
/// part of this payload — never trust a client-supplied <c>createdBy</c>.
/// </para>
/// <para>
/// <b>Agent identity (<c>AgentId</c>) is also resolved server-side</b> via
/// <c>IAgentRunReader.GetRunsForThreadAsync(RunId)</c> — the widget's mount
/// surface (Automate's <c>Ua.Modal.RunDetail</c>) supplies only the workflow
/// run identifier in <c>modalContext.data.runId</c>, never an agent id. Story
/// 2.3 Task 0 spike empirically established that the original 4-field contract
/// (<c>runId, agentId, score, comment</c>) was over-specified; the controller
/// now derives <c>agentId</c> from the run.
/// </para>
/// <para>
/// <b>Per-iteration selection (Story 4.12 picker; LD#8 ratified 2026-05-21):</b>
/// <see cref="SelectedRunId"/> is optional. When supplied, the controller
/// resolves <see cref="AgentId"/> from the sibling iteration whose
/// <c>Metadata.Umbraco.AI.Agent.RunId</c> equals
/// <see cref="SelectedRunId"/> + records feedback under
/// <c>feedbackRunId = SelectedRunId</c> (creating a distinct supersede key
/// per iteration so a batch review session can teach N iterations
/// independently). When omitted, the legacy ThreadId-keyed path is preserved
/// byte-compatibly with Story 2.3 + Story 4.5. The
/// <c>cogworks_agent_memory_feedback.RunId</c> column accordingly carries
/// DUAL semantics post-Story-4.12: legacy/non-picker rows store ThreadId
/// values (Story 2.3 Path b convention); picker rows store per-iteration
/// RunId values. v0.2 schema-rename story queued — see Story 4.12 § Forward-
/// pointer hooks #1.
/// </para>
/// </remarks>
public sealed record AgentFeedbackPostRequest(
    /// <summary>
    /// The workflow run identifier from the editor's feedback surface — i.e.
    /// the value the Bellissima modal hands the widget in
    /// <c>modalContext.data.runId</c>. Semantically this is the upstream
    /// <c>Metadata["Umbraco.AI.Agent.ThreadId"]</c> (workflow-run-level
    /// conversation grouping key; 1 per Automate workflow run, shared across
    /// all <c>RunAgentAction.ExecuteAsync</c> invocations within). The package
    /// stores this in its <c>RunId</c> column for Path (b) naming continuity
    /// per Story 2.3 § Locked decisions; a future post-keynote story may
    /// rename the column to <c>ThreadId</c> (Path a) per AR8.
    /// </summary>
    string RunId,
    FeedbackScore Score,
    string? Comment,
    /// <summary>
    /// Story 4.12 picker — optional per-iteration agent-invocation key
    /// (<c>Metadata.Umbraco.AI.Agent.RunId</c>). Supplied by the widget when
    /// the editor submits feedback against a specific iteration of a For Each
    /// batch workflow; omitted on legacy/non-picker submissions for
    /// byte-compatibility with Story 2.3 + Story 4.5.
    /// </summary>
    string? SelectedRunId = null);
